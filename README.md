# Sentinel — پرتال اختصاصی مشتریان

پرتالی برای مشتریان فعال سرویس، که در آن هر کاربر فقط به برنامه‌هایی دسترسی دارد که
اشتراک یا مجوز اختصاصی معتبر برای آن‌ها داشته باشد.

ASP.NET Core 10 · Entity Framework Core 10 · Razor Views · Vue 3 به‌صورت جزیره‌ای per-page
· PostgreSQL / SQL Server / SQLite

---

## فهرست

- [معماری](#معماری)
- [پیش‌نیازها](#پیش‌نیازها)
- [اجرای سریع محلی](#اجرای-سریع-محلی)
- [اجرا با Docker](#اجرا-با-docker)
- [دیتابیس و Migration](#دیتابیس-و-migration)
- [فرانت‌اند](#فرانتاند)
- [تست](#تست)
- [پیکربندی](#پیکربندی)
- [امنیت](#امنیت)
- [وابستگی‌ها](#وابستگیها)
- [وضعیت فعلی](#وضعیت-فعلی)

---

## معماری

```
src/
  Sentinel.Domain          موجودیت‌ها، enumها و قواعد خالص دامنه. بدون EF Core و ASP.NET Core.
  Sentinel.Application     قراردادها (ISentinelDbContext)، سرویس‌های دامنه، DTOها، Query Serviceها.
  Sentinel.Infrastructure  DbContext، پیکربندی موجودیت‌ها، Migration، Seed، پیاده‌سازی سرویس‌ها.
  Sentinel.Web             MVC، Razor Views، میان‌افزارهای امنیتی، Identity، Vue islands.
tests/
  Sentinel.UnitTests        منطق خالص (قواعد دسترسی، Audit metadata).
  Sentinel.IntegrationTests کل اپلیکیشن روی SQLite، بدون هیچ mock در مسیر احراز هویت.
```

### مدل دسترسی

دو موتور تصمیم‌گیری، هر دو تابع خالص، هر دو کاملاً unit-test شده:

- **`MembershipStatusResolver`** — وضعیت مؤثر عضویت را از روی تاریخ‌ها محاسبه می‌کند.
  `MembershipStatus` هرگز در دیتابیس ذخیره نمی‌شود؛ یک ستون «Expired» به یک job زمان‌بندی‌شده
  نیاز داشت و بین دو اجرا بی‌صدا اشتباه می‌بود.
- **`AccessRuleEvaluator`** — پاسخ «آیا این کاربر اجازهٔ باز کردن این برنامه را دارد؟».

ترتیب قوانین در `AccessRuleEvaluator` معنادار است:

1. **حساب** — غیرفعال یا مسدود؟ هیچ‌چیز دیگری بررسی نمی‌شود.
2. **برنامه** — غیرفعال یا منتشرنشده؟ برای همه بسته است، حتی دارندهٔ دسترسی اختصاصی.
3. **دسترسی اختصاصی معتبر** → مجاز. این دقیقاً کارکرد Entitlement است: یک ترتیب فردی که به
   زندهبودن اشتراک وابسته نیست و سطح عضویت را هم دور می‌زند.
4. برنامه‌های `RequiresExplicitEntitlement` همین‌جا متوقف می‌شوند؛ عضویت به‌تنهایی بازشان نمی‌کند.
5. در غیر این صورت **عضویت** تصمیم می‌گیرد: معتبر بودن، سپس `MinimumTier`.

`IAccessDecisionService` فقط ورودی‌ها را از دیتابیس جمع می‌کند و به Evaluator می‌دهد. همان یک
تابع، هم فهرست برنامه‌ها و هم endpoint اجرا را تغذیه می‌کند — اگر فهرست، دسترسی را جور دیگری
حساب می‌کرد، دیر یا زود با endpoint اختلاف پیدا می‌کرد و همان اختلاف، باگ امنیتی می‌شد.

**اجرای برنامه هرگز از سمت کلاینت نیست.** آدرس مقصد اصلاً به مرورگر فرستاده نمی‌شود؛ کارت فقط
به `/apps/{key}/open` لینک می‌دهد، سرور دوباره تصمیم می‌گیرد، URL را دوباره اعتبارسنجی می‌کند،
رویداد را ثبت می‌کند و بعد redirect می‌دهد. به همین دلیل قفلِ روی کارت یک کنترل واقعی است، نه
یک دکمهٔ مخفی.

چند تصمیم که آگاهانه گرفته شده‌اند:

- **Repository جنریک وجود ندارد.** `DbSet<T>` خودش یک repository است؛ پوشاندن آن فقط کیفیت
  Query را پایین می‌آورد. لایهٔ Application مستقیماً روی `ISentinelDbContext` کوئری می‌نویسد و
  هرجا کوئری پیچیده شد، به یک Query Service نام‌دار تبدیل می‌شود.
- **Business logic داخل Controller نیست.** برای نمونه کل تصمیم ورود در `PortalSignInService`
  است، نه در `AccountController`.
- **هیچ‌جا `DateTimeOffset.UtcNow` صدا زده نمی‌شود.** همه‌جا `TimeProvider` تزریق می‌شود تا
  منطق انقضا و Grace Period قابل تست باشد.
- **وابستگی به دیتابیس خاص حداقل است.** فقط `AddSentinelPersistence` می‌داند کدام موتور فعال
  است؛ پیکربندی موجودیت‌ها از نوع ستون provider-specific استفاده نمی‌کند.

---

## پیش‌نیازها

| ابزار | نسخه | ضروری؟ |
|---|---|---|
| .NET SDK | 10.0 | بله |
| Node.js | 20+ | فقط برای تغییر فایل‌های `Scripts/` |
| Docker | جدید | فقط برای اجرای کانتینری |
| PostgreSQL | 15+ | فقط برای اجرای غیرکانتینری روی Postgres |

خروجی build فرانت‌اند در `wwwroot/js/dist/` کامیت شده است، بنابراین `dotnet run` بدون نصب
Node هم کار می‌کند.

---

## اجرای سریع محلی

سریع‌ترین مسیر: SQLite، بدون نیاز به هیچ سرور دیتابیسی.

نخست اولین ادمین را از طریق user-secrets تعریف کنید (رمز هرگز داخل فایل‌های پروژه نوشته
نمی‌شود):

```bash
cd src/Sentinel.Web
dotnet user-secrets set "Seed:SuperAdmin:Enabled" "true"
dotnet user-secrets set "Seed:SuperAdmin:UserName" "admin"
dotnet user-secrets set "Seed:SuperAdmin:Email" "admin@example.com"
dotnet user-secrets set "Seed:SuperAdmin:Password" "<یک رمز حداقل ۱۲ کاراکتری>"
```

سپس اجرا کنید:

```bash
dotnet run --project src/Sentinel.Web
```

پرتال روی `https://localhost:7238` بالا می‌آید. پروفایل `http` برای اجرای بدون TLS:

```bash
dotnet run --project src/Sentinel.Web --launch-profile http
```

پس از نخستین ورود، `Seed:SuperAdmin:Enabled` را `false` کنید و رمز را از secret store پاک
کنید:

```bash
dotnet user-secrets remove "Seed:SuperAdmin:Password" --project src/Sentinel.Web
```

---

## اجرا با Docker

```bash
cp .env.example .env
```

`.env` را پر کنید (دست‌کم `POSTGRES_PASSWORD`، و برای نخستین اجرا مقادیر `SEED_SUPERADMIN_*`)،
سپس:

```bash
docker compose up --build
```

نکات مهم برای Production:

- `sentinel-keys` را حتماً به‌صورت volume نگه دارید؛ در غیر این صورت هر redeploy کلیدهای
  Data Protection را از بین می‌برد و همهٔ کاربران از حساب خارج می‌شوند.
- `FORWARDED_HEADER_HOPS` را فقط وقتی بزرگ‌تر از صفر بگذارید که یک reverse proxy تحت کنترل
  خودتان تنها راه ورود باشد.
- پس از ساخت اولین ادمین، `SEED_SUPERADMIN_*` را خالی کنید و دوباره deploy کنید.

---

## دیتابیس و Migration

Migrationهای موجود در این مخزن برای **PostgreSQL** تولید شده‌اند.

| Provider | نحوهٔ ساخت schema | کاربرد |
|---|---|---|
| `PostgreSql` | Migration (`Database:MigrateOnStartup` یا دستور جداگانه) | Production |
| `Sqlite` | `EnsureCreated` از روی مدل | توسعهٔ محلی و تست |
| `SqlServer` | Migration اختصاصی خودتان | پشتیبانی‌شده، ولی migration همراه ندارد |

ساخت migration جدید:

```bash
dotnet ef migrations add <Name> --project src/Sentinel.Infrastructure --output-dir Persistence/Migrations
```

اعمال migrationها روی یک دیتابیس واقعی:

```bash
SENTINEL_MIGRATIONS_CONNECTION="Host=localhost;Database=sentinel;Username=sentinel;Password=..." \
  dotnet ef database update --project src/Sentinel.Infrastructure
```

تولید اسکریپت SQL برای اعمال دستی (روش پیشنهادی برای Production):

```bash
dotnet ef migrations script --project src/Sentinel.Infrastructure --idempotent -o migrate.sql
```

### اگر SQL Server می‌خواهید

`Database:Provider` را `SqlServer` بگذارید و یک مجموعه migration مخصوص خودش بسازید. چون
EF نمی‌تواند دو مجموعه migration را در یک اسمبلی از هم تفکیک کند، این کار به یک پروژهٔ
migration جداگانه با `MigrationsAssembly` نیاز دارد. تا آن زمان `MigrateOnStartup` برای
SQL Server عمداً با پیام صریح خطا رد می‌شود تا DDL مخصوص Postgres روی SQL Server اجرا نشود.

---

## فرانت‌اند

Vue به‌صورت SPA استفاده نمی‌شود. Routing، render و SEO با ASP.NET Core MVC است و هر صفحه
می‌تواند جزیرهٔ Vue کوچک خودش را داشته باشد.

```bash
cd src/Sentinel.Web
npm install
npm run build      # یک‌بار
npm run watch      # هنگام توسعه
```

خروجی در `wwwroot/js/dist/` می‌نشیند و کامیت می‌شود.

**چرا build step وجود دارد؟** نسخهٔ کاملِ مرورگری Vue برای کامپایل template از `new Function`
استفاده می‌کند و اجرای آن نیازمند `unsafe-eval` در CSP است — که عملاً بیشتر فایدهٔ CSP را از
بین می‌برد. با Vite، templateها در زمان build کامپایل می‌شوند و runtime-only build بارگذاری
می‌شود؛ به همین دلیل CSP این پروژه نه `unsafe-eval` دارد و نه `unsafe-inline` برای script.

صفحهٔ ورود عمداً بدون Vue است: این تنها صفحه‌ای است که باید حتی با JavaScript خاموش هم کار کند،
پس Razor فرم واقعی را render می‌کند و JS ساده فقط قابلیت‌های اضافه (نمایش رمز، هشدار Caps Lock،
جلوگیری از ارسال دوباره) را می‌افزاید.

جزیره‌های فعلی Vue:

| جزیره | صفحه | چرا Vue |
|---|---|---|
| `AppGrid` | برنامه‌های من | جست‌وجو و فیلتر، وضعیت واقعی سمت کلاینت |
| `AppCard` | برنامه‌های من + داشبورد | یک پیاده‌سازی کارت برای هر دو صفحه |

داده از یک attribute به‌نام `data-apps` خوانده می‌شود، نه از `<script type="application/json">`.
دلیلش مشخص است: Razor مقدار attribute را encode می‌کند و `dataset` آن را decode می‌کند، پس داده
تحت هیچ شرایطی نمی‌تواند از context خودش بیرون بزند — در حالی که بلوک inline به `@Html.Raw` و
قواعد escape جداگانه نیاز داشت. در کل پروژه حتی یک `Html.Raw` وجود ندارد.

تمام رشته‌هایی که جزیره render می‌کند از سرور و از پیش ترجمه‌شده می‌رسند، بنابراین فقط یک
catalogue ترجمه وجود دارد و هیچ منطق زبانی داخل JS نیست. هر صفحه‌ای که جزیره دارد، یک
`<noscript>` با فهرست ساده و کاملاً کارآمد هم دارد.

---

## تست

```bash
dotnet test
```

تست‌های Integration روی SQLite درون‌حافظه‌ای اجرا می‌شوند: بدون Docker، بدون سرور، و در عمل
اثبات می‌کنند که مدل به Postgres گره نخورده است. هیچ بخشی از مسیر احراز هویت یا مجوزدهی در
تست‌ها mock نشده است.

اجرای یک گروه مشخص:

```bash
dotnet test --filter "FullyQualifiedName~SecurityTests"
```

---

## پیکربندی

`src/Sentinel.Web/appsettings.Example.json` تمام کلیدها را با توضیح فهرست کرده است. متغیرهای
محیطی با دو زیرخط تودرتو می‌شوند:

```bash
Database__Provider=PostgreSql
ConnectionStrings__Sentinel="Host=db;Database=sentinel;Username=sentinel;Password=..."
Seed__SuperAdmin__Password="..."
```

مهم‌ترین کلیدها:

| کلید | پیش‌فرض | توضیح |
|---|---|---|
| `Database:Provider` | `PostgreSql` | `PostgreSql` \| `Sqlite` \| `SqlServer` |
| `Database:MigrateOnStartup` | `false` | برای چند replica خاموش بماند |
| `Security:RequireHttps` | `true` | HSTS، ریدایرکت HTTPS و کوکی با پیشوند `__Host-` |
| `Security:SessionLifetimeMinutes` | `480` | طول عمر نشست |
| `Security:ForwardedHeaderHops` | `0` | تعداد proxyهای مورد اعتماد |
| `Security:Password:MinimumLength` | `12` | حداقل طول رمز |
| `Security:Lockout:MaxFailedAttempts` | `5` | قفل موقت حساب |
| `Security:LoginRateLimit:PermitLimit` | `10` | سقف تلاش ورود در هر پنجره، به ازای هر IP |
| `Membership:GracePeriodDays` | `3` | مهلت پس از انقضا |
| `DataProtection:KeyRingPath` | خالی | مسیر نگهداری کلیدهای رمزنگاری کوکی |

هیچ secret‌ای در فایل‌های مخزن نیست. در توسعه از `dotnet user-secrets` و در Production از
متغیر محیطی یا secret store استفاده کنید.

چند تنظیم در Production عمداً باعث **خطای startup** می‌شوند (نه هشدار):
`Database:EnableSensitiveDataLogging`، `Security:RequireHttps=false`، `Database:Provider=Sqlite`
و `Seed:IncludeSampleApplications`.

---

## امنیت

| موضوع | اقدام |
|---|---|
| SQL Injection | تمام کوئری‌ها LINQ روی EF Core و پارامتری؛ هیچ Raw SQL و هیچ الحاق رشته‌ای در پروژه نیست |
| XSS | encode پیش‌فرض Razor؛ هیچ `Html.Raw` و هیچ `v-html` استفاده نشده؛ helperهای JS فقط `textContent` می‌نویسند |
| CSP | سیاست سخت‌گیرانه با nonce؛ بدون `unsafe-eval`، بدون `unsafe-inline` برای script؛ nonce در هر پاسخ تازه تولید می‌شود |
| CSRF | `AutoValidateAntiforgeryToken` به‌صورت سراسری؛ همهٔ verbهای تغییردهنده پوشش دارند؛ کوکی `SameSite=Strict` و `HttpOnly` |
| Session | نشست سمت سرور در جدول `UserSessions`؛ خروج، ردیف را باطل می‌کند تا کوکی سرقت‌شده بلافاصله بی‌اثر شود |
| Session Fixation | پیش از صدور کوکی جدید، کوکی قبلی صریحاً باطل می‌شود |
| User Enumeration | همهٔ حالت‌های شکست یک پیام یکسان می‌دهند؛ برای کاربر ناموجود هم یک hash محاسبه می‌شود تا زمان پاسخ لو ندهد |
| Brute Force | Lockout به‌ازای حساب (Identity) + Rate Limit به‌ازای IP روی endpoint ورود |
| Authorization | Policy-based؛ `FallbackPolicy` یعنی هر endpointی که فراموش شود همچنان بسته است؛ `AllowAnonymous` فقط روی اکشن، نه Controller |
| IDOR | داشبورد کاربر اصلاً id از request نمی‌گیرد؛ شناسه همیشه از principal احراز هویت‌شده خوانده می‌شود |
| Open Redirect | هر `returnUrl` با `Url.IsLocalUrl` اعتبارسنجی می‌شود؛ تست برای `//host`، `/\host` و مشابه وجود دارد |
| Headers | HSTS، `X-Content-Type-Options`، `Referrer-Policy`، `Permissions-Policy`، `frame-ancestors`، COOP/CORP؛ حذف `Server` |
| Mass Assignment | ورودی‌ها فقط از طریق ViewModel/DTO؛ هیچ entity ای مستقیم bind نمی‌شود |
| Audit | عملیات حساس در `AuditLogs` ثبت می‌شود؛ کلیدهای metadata در برابر الگوهای secret غربال می‌شوند و در صورت تخلف exception می‌دهند |
| Error Handling | در Production فقط صفحهٔ عمومی + Correlation ID؛ جزئیات فقط در لاگ |
| Secrets | هیچ رمز/توکنی در سورس، لاگ یا Audit نوشته نمی‌شود |

موارد باقی‌مانده و آگاهانه به تعویق افتاده در [وضعیت فعلی](#وضعیت-فعلی) فهرست شده‌اند.

---

## وابستگی‌ها

نسخه‌ها به‌صورت متمرکز در `Directory.Packages.props` مدیریت می‌شوند و `NuGet.config` منبع را
فقط به nuget.org محدود می‌کند تا هیچ پکیجی از فید ناشناخته کشیده نشود.

| پکیج | چرا |
|---|---|
| `Microsoft.EntityFrameworkCore` (+ `.Relational`, `.Design`) | ORM و ابزار migration |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | provider اصلی Production |
| `Microsoft.EntityFrameworkCore.Sqlite` | توسعهٔ بدون سرور و تست‌های Integration |
| `Microsoft.EntityFrameworkCore.SqlServer` | provider جایگزین در زمان اجرا |
| `Microsoft.AspNetCore.Identity.EntityFrameworkCore` | احراز هویت، hash رمز، lockout، security stamp |
| `Microsoft.Extensions.Identity.Stores` | فقط مدل‌های Identity در لایهٔ Domain، بدون کشاندن EF یا ASP.NET به آنجا |
| `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` | readiness واقعی (اتصال به دیتابیس) |
| `Serilog.AspNetCore` + `.Sinks.Console` + `.Formatting.Compact` | لاگ ساخت‌یافتهٔ JSON برای ابزارهای تجمیع لاگ |
| `SQLitePCLRaw.*` (pin شده روی 2.1.12) | EF نسخهٔ 2.1.11 را می‌آورد که NuGet audit آن را آسیب‌پذیر علامت می‌زند |
| `vue` | جزیره‌های تعاملی per-page |
| `vite` | کامپایل template در زمان build تا CSP به `unsafe-eval` نیاز نداشته باشد |
| `@fontsource-variable/vazirmatn` | فونت self-hosted؛ CSP اجازهٔ CDN نمی‌دهد |
| xUnit، `Microsoft.AspNetCore.Mvc.Testing`، `Microsoft.Extensions.TimeProvider.Testing` | تست |

`dotnet restore` بدون هیچ هشدار آسیب‌پذیری تمام می‌شود.

---

## وضعیت فعلی

**فاز ۱ کامل است:** دامنه، persistence، Identity، نشست‌های سمت سرور، میان‌افزارهای امنیتی،
Rate Limit، Audit، محلی‌سازی fa/en، تم روشن/تیره/خودکار، صفحهٔ ورود، صفحات خطا، Health Check،
Migration اولیهٔ Postgres و Docker.

**فاز ۲ کامل است:** `MembershipStatusResolver`، `AccessRuleEvaluator`، `AccessDecisionService`،
endpoint اجرای برنامه با ثبت Audit، سیاست اعتبارسنجی URL، داشبورد کامل، صفحهٔ برنامه‌های من با
جزیرهٔ Vue، و صفحهٔ عضویت.

**۱۸۳ تست سبز** (۱۰۹ unit + ۷۴ integration).

| فاز | محتوا | وضعیت |
|---|---|---|
| ۱ | زیرساخت، احراز هویت، نشست، امنیت | ✅ |
| ۲ | تصمیم دسترسی، عضویت، پرتال کاربر | ✅ |
| ۳ | مدیریت کاربران و عضویت‌ها (صفحه‌بندی سمت سرور، تغییر وضعیت، تاریخ عضویت) | — |
| ۴ | مدیریت برنامه‌ها و Entitlementها، آپلود آیکون | — |
| ۵ | پروفایل، امنیت حساب، نمایشگر Audit، تنظیمات، CI | — |

محدودیت‌های شناخته‌شدهٔ فعلی:

- **هنوز رابط کاربری مدیریتی وجود ندارد.** ساخت عضویت و اعطای دسترسی اختصاصی فعلاً فقط با
  درج مستقیم در دیتابیس ممکن است؛ فاز ۳ و ۴ این را حل می‌کنند. موتور تصمیم‌گیری کامل است و
  به‌محض وجود UI کار می‌کند.
- ورود با **شماره تلفن** پیاده نشده است. برای آنکه امن باشد به یک ستون نرمال‌شده با
  unique index نیاز دارد که فیلترش provider-specific است؛ همراه با مدیریت کاربران در فاز ۳.
- برای **SQL Server** مجموعه migration همراه پروژه نیست (بالا توضیح داده شد).
- اعتبارسنجی نشست در هر درخواست یک lookup روی کلید اصلی می‌زند و عمداً cache نمی‌شود، تا
  ابطال نشست بلافاصله و روی همهٔ replicaها اثر کند.
- `Seed:IncludeSampleApplications` علاوه بر برنامه‌های نمونه، به هر حسابی که عضویت ندارد یک
  عضویت فعال می‌دهد تا پرتال روی دیتابیس تازه قابل‌مشاهده باشد. این رفتار در Production با
  خطای startup رد می‌شود.
