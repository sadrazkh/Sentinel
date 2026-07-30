using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Products;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class ProductSectionConfiguration : IEntityTypeConfiguration<ProductSection>
{
    public void Configure(EntityTypeBuilder<ProductSection> builder)
    {
        builder.ToTable("ProductSections");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.TitleFa).HasMaxLength(ProductSection.TitleMaxLength);
        builder.Property(s => s.TitleEn).HasMaxLength(ProductSection.TitleMaxLength);
        builder.Property(s => s.MarkupFa).HasMaxLength(ProductSection.MarkupMaxLength);
        builder.Property(s => s.MarkupEn).HasMaxLength(ProductSection.MarkupMaxLength);
        builder.Property(s => s.BodyHtmlFa).HasMaxLength(ProductSection.BodyHtmlMaxLength);
        builder.Property(s => s.BodyHtmlEn).HasMaxLength(ProductSection.BodyHtmlMaxLength);

        builder.Property(s => s.Kind).HasConversion<int>().IsRequired();
        builder.Property(s => s.Visibility).HasConversion<int>().IsRequired();

        builder.Property(s => s.ConcurrencyToken).IsConcurrencyToken();

        // Sections belong to their product: deleting the product should take its page with it.
        builder.HasOne(s => s.Product)
            .WithMany()
            .HasForeignKey(s => s.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.ProductId, s.DisplayOrder });
    }
}

public sealed class ProductDownloadConfiguration : IEntityTypeConfiguration<ProductDownload>
{
    public void Configure(EntityTypeBuilder<ProductDownload> builder)
    {
        builder.ToTable("ProductDownloads");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.TitleFa).HasMaxLength(ProductDownload.TitleMaxLength).IsRequired();
        builder.Property(d => d.TitleEn).HasMaxLength(ProductDownload.TitleMaxLength).IsRequired();
        builder.Property(d => d.NoteFa).HasMaxLength(ProductDownload.NoteMaxLength);
        builder.Property(d => d.NoteEn).HasMaxLength(ProductDownload.NoteMaxLength);
        builder.Property(d => d.Url).HasMaxLength(ProductDownload.UrlMaxLength).IsRequired();
        builder.Property(d => d.Version).HasMaxLength(ProductDownload.VersionMaxLength);
        builder.Property(d => d.Checksum).HasMaxLength(ProductDownload.ChecksumMaxLength);

        builder.Property(d => d.Platform).HasConversion<int>().IsRequired();
        builder.Property(d => d.Visibility).HasConversion<int>().IsRequired();

        builder.Property(d => d.ConcurrencyToken).IsConcurrencyToken();

        builder.HasOne(d => d.Product)
            .WithMany()
            .HasForeignKey(d => d.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(d => new { d.ProductId, d.Platform, d.DisplayOrder });
    }
}

public sealed class DocumentationCategoryConfiguration : IEntityTypeConfiguration<DocumentationCategory>
{
    public void Configure(EntityTypeBuilder<DocumentationCategory> builder)
    {
        builder.ToTable("DocumentationCategories");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Slug).HasMaxLength(DocumentationCategory.SlugMaxLength).IsRequired();
        builder.Property(c => c.TitleFa).HasMaxLength(DocumentationCategory.TitleMaxLength).IsRequired();
        builder.Property(c => c.TitleEn).HasMaxLength(DocumentationCategory.TitleMaxLength).IsRequired();
        builder.Property(c => c.IconName).HasMaxLength(64);

        builder.Property(c => c.ConcurrencyToken).IsConcurrencyToken();

        builder.HasOne(c => c.Product)
            .WithMany()
            .HasForeignKey(c => c.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique per product, not globally: two products may both have a "getting-started"
        // without either having to invent a name to avoid the other.
        builder.HasIndex(c => new { c.ProductId, c.Slug }).IsUnique();
    }
}

public sealed class DocumentationArticleConfiguration : IEntityTypeConfiguration<DocumentationArticle>
{
    public void Configure(EntityTypeBuilder<DocumentationArticle> builder)
    {
        builder.ToTable("DocumentationArticles");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Slug).HasMaxLength(DocumentationArticle.SlugMaxLength).IsRequired();
        builder.Property(a => a.TitleFa).HasMaxLength(DocumentationArticle.TitleMaxLength).IsRequired();
        builder.Property(a => a.TitleEn).HasMaxLength(DocumentationArticle.TitleMaxLength).IsRequired();
        builder.Property(a => a.SummaryFa).HasMaxLength(DocumentationArticle.SummaryMaxLength);
        builder.Property(a => a.SummaryEn).HasMaxLength(DocumentationArticle.SummaryMaxLength);
        builder.Property(a => a.MarkupFa).HasMaxLength(DocumentationArticle.MarkupMaxLength);
        builder.Property(a => a.MarkupEn).HasMaxLength(DocumentationArticle.MarkupMaxLength);
        builder.Property(a => a.BodyHtmlFa).HasMaxLength(DocumentationArticle.BodyHtmlMaxLength);
        builder.Property(a => a.BodyHtmlEn).HasMaxLength(DocumentationArticle.BodyHtmlMaxLength);

        builder.Property(a => a.Visibility).HasConversion<int>().IsRequired();
        builder.Property(a => a.Platform).HasConversion<int?>();

        builder.Property(a => a.ConcurrencyToken).IsConcurrencyToken();

        builder.HasOne(a => a.Product)
            .WithMany()
            .HasForeignKey(a => a.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // Removing a category must not delete the articles filed under it — they become
        // uncategorised and stay readable.
        builder.HasOne(a => a.Category)
            .WithMany(c => c.Articles)
            .HasForeignKey(a => a.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(a => new { a.ProductId, a.Slug }).IsUnique();
        builder.HasIndex(a => new { a.ProductId, a.IsPublished, a.DisplayOrder });
    }
}

public sealed class DocumentationStepConfiguration : IEntityTypeConfiguration<DocumentationStep>
{
    public void Configure(EntityTypeBuilder<DocumentationStep> builder)
    {
        builder.ToTable("DocumentationSteps");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.TitleFa).HasMaxLength(DocumentationStep.TitleMaxLength);
        builder.Property(s => s.TitleEn).HasMaxLength(DocumentationStep.TitleMaxLength);
        builder.Property(s => s.BodyFa).HasMaxLength(DocumentationStep.BodyMaxLength);
        builder.Property(s => s.BodyEn).HasMaxLength(DocumentationStep.BodyMaxLength);
        builder.Property(s => s.MediaPath).HasMaxLength(DocumentationStep.MediaPathMaxLength);

        builder.HasOne(s => s.Article)
            .WithMany(a => a.Steps)
            .HasForeignKey(s => s.ArticleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.ArticleId, s.StepNumber }).IsUnique();
    }
}
