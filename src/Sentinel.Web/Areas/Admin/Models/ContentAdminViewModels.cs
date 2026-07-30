using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Sentinel.Application.Content;
using Sentinel.Application.Products;
using Sentinel.Domain.Products;

namespace Sentinel.Web.Areas.Admin.Models;

/// <summary>The content overview for one product: what exists, and the links to change it.</summary>
public sealed class ContentOverviewViewModel
{
    public required Guid ProductId { get; init; }

    public required string ProductKey { get; init; }

    public required string ProductNameFa { get; init; }

    public required string ProductNameEn { get; init; }

    public required ProductContentSummary Summary { get; init; }

    public required IReadOnlyList<ProductSectionEditModel> Sections { get; init; }

    public required IReadOnlyList<ProductDownloadEditModel> Downloads { get; init; }

    public required IReadOnlyList<DocumentationCategoryOption> Categories { get; init; }

    public required IReadOnlyList<DocumentationArticleEditModel> Articles { get; init; }

    public required bool CanWrite { get; init; }
}

public sealed class SectionEditViewModel
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    [Display(Name = "admin.content.kind")]
    public ProductSectionKind Kind { get; set; } = ProductSectionKind.Text;

    [Display(Name = "admin.content.visibility")]
    public ContentVisibility Visibility { get; set; } = ContentVisibility.Public;

    [StringLength(ProductSection.TitleMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameFa")]
    public string? TitleFa { get; set; }

    [StringLength(ProductSection.TitleMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameEn")]
    public string? TitleEn { get; set; }

    [StringLength(ProductSection.MarkupMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.content.markupFa")]
    public string? MarkupFa { get; set; }

    [StringLength(ProductSection.MarkupMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.content.markupEn")]
    public string? MarkupEn { get; set; }

    [Range(0, 10_000, ErrorMessage = "validation.range")]
    [Display(Name = "admin.application.displayOrder")]
    public int DisplayOrder { get; set; } = 100;

    [Display(Name = "admin.application.isEnabled")]
    public bool IsVisible { get; set; } = true;

    public Guid? ConcurrencyToken { get; set; }

    public bool IsNew => Id == Guid.Empty;

    public static SectionEditViewModel From(ProductSectionEditModel model) => new()
    {
        Id = model.Id,
        ProductId = model.ProductId,
        Kind = model.Kind,
        Visibility = model.Visibility,
        TitleFa = model.TitleFa,
        TitleEn = model.TitleEn,
        MarkupFa = model.MarkupFa,
        MarkupEn = model.MarkupEn,
        DisplayOrder = model.DisplayOrder,
        IsVisible = model.IsVisible,
        ConcurrencyToken = model.ConcurrencyToken,
    };

    public ProductSectionSaveRequest ToRequest() => new(
        Kind, Visibility, TitleFa, TitleEn, MarkupFa, MarkupEn,
        DisplayOrder, IsVisible, ConcurrencyToken);
}

public sealed class DownloadEditViewModel
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    [Display(Name = "admin.content.platform")]
    public DownloadPlatform Platform { get; set; } = DownloadPlatform.Any;

    [Display(Name = "admin.content.visibility")]
    public ContentVisibility Visibility { get; set; } = ContentVisibility.Entitled;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(ProductDownload.TitleMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameFa")]
    public string TitleFa { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(ProductDownload.TitleMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameEn")]
    public string TitleEn { get; set; } = string.Empty;

    [StringLength(ProductDownload.NoteMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.descriptionFa")]
    public string? NoteFa { get; set; }

    [StringLength(ProductDownload.NoteMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.descriptionEn")]
    public string? NoteEn { get; set; }

    /// <summary>
    /// Length-checked here only. The scheme and host rules live in
    /// <see cref="DownloadUrlPolicy"/>, which both this form and the redirect use — duplicating
    /// them in an attribute would create a second, drift-prone copy.
    /// </summary>
    [Required(ErrorMessage = "validation.required")]
    [StringLength(ProductDownload.UrlMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.content.url")]
    public string Url { get; set; } = string.Empty;

    [StringLength(ProductDownload.VersionMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.content.version")]
    public string? Version { get; set; }

    [StringLength(ProductDownload.ChecksumMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.content.checksum")]
    public string? Checksum { get; set; }

    [Range(0, long.MaxValue, ErrorMessage = "validation.range")]
    [Display(Name = "admin.content.size")]
    public long? SizeBytes { get; set; }

    [Range(0, 10_000, ErrorMessage = "validation.range")]
    [Display(Name = "admin.application.displayOrder")]
    public int DisplayOrder { get; set; } = 100;

    [Display(Name = "admin.application.isEnabled")]
    public bool IsVisible { get; set; } = true;

    public Guid? ConcurrencyToken { get; set; }

    public bool IsNew => Id == Guid.Empty;

    public static DownloadEditViewModel From(ProductDownloadEditModel model) => new()
    {
        Id = model.Id,
        ProductId = model.ProductId,
        Platform = model.Platform,
        Visibility = model.Visibility,
        TitleFa = model.TitleFa,
        TitleEn = model.TitleEn,
        NoteFa = model.NoteFa,
        NoteEn = model.NoteEn,
        Url = model.Url,
        Version = model.Version,
        Checksum = model.Checksum,
        SizeBytes = model.SizeBytes,
        DisplayOrder = model.DisplayOrder,
        IsVisible = model.IsVisible,
        ConcurrencyToken = model.ConcurrencyToken,
    };

    public ProductDownloadSaveRequest ToRequest() => new(
        Platform, Visibility, TitleFa, TitleEn, NoteFa, NoteEn, Url,
        Version, Checksum, SizeBytes, DisplayOrder, IsVisible, ConcurrencyToken);
}

public sealed class ArticleEditViewModel
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    [Display(Name = "admin.content.category")]
    public Guid? CategoryId { get; set; }

    [StringLength(ContentSlug.MaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.content.slug")]
    public string? Slug { get; set; }

    [Required(ErrorMessage = "validation.required")]
    [StringLength(DocumentationArticle.TitleMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameFa")]
    public string TitleFa { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(DocumentationArticle.TitleMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameEn")]
    public string TitleEn { get; set; } = string.Empty;

    [StringLength(DocumentationArticle.SummaryMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.content.summaryFa")]
    public string? SummaryFa { get; set; }

    [StringLength(DocumentationArticle.SummaryMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.content.summaryEn")]
    public string? SummaryEn { get; set; }

    [StringLength(DocumentationArticle.MarkupMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.content.markupFa")]
    public string? MarkupFa { get; set; }

    [StringLength(DocumentationArticle.MarkupMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.content.markupEn")]
    public string? MarkupEn { get; set; }

    [Display(Name = "admin.content.visibility")]
    public ContentVisibility Visibility { get; set; } = ContentVisibility.Public;

    [Display(Name = "admin.content.platform")]
    public DownloadPlatform? Platform { get; set; }

    [Range(0, 10_000, ErrorMessage = "validation.range")]
    [Display(Name = "admin.application.displayOrder")]
    public int DisplayOrder { get; set; } = 100;

    [Display(Name = "admin.content.published")]
    public bool IsPublished { get; set; }

    public Guid? ConcurrencyToken { get; set; }

    public bool IsNew => Id == Guid.Empty;

    /// <summary>
    /// Bound as a parallel array of the step fields. Order in the posted arrays is the step
    /// order, and the service renumbers from one — so an operator never types a step number and
    /// two steps can never claim the same one.
    /// </summary>
    public List<StepFieldSet> Steps { get; set; } = [];

    public static ArticleEditViewModel From(DocumentationArticleEditModel model) => new()
    {
        Id = model.Id,
        ProductId = model.ProductId,
        CategoryId = model.CategoryId,
        Slug = model.Slug,
        TitleFa = model.TitleFa,
        TitleEn = model.TitleEn,
        SummaryFa = model.SummaryFa,
        SummaryEn = model.SummaryEn,
        MarkupFa = model.MarkupFa,
        MarkupEn = model.MarkupEn,
        Visibility = model.Visibility,
        Platform = model.Platform,
        DisplayOrder = model.DisplayOrder,
        IsPublished = model.IsPublished,
        ConcurrencyToken = model.ConcurrencyToken,
    };

    public DocumentationArticleSaveRequest ToRequest() => new(
        CategoryId, Slug, TitleFa, TitleEn, SummaryFa, SummaryEn,
        MarkupFa, MarkupEn, Visibility, Platform, DisplayOrder, IsPublished, ConcurrencyToken);

    /// <summary>
    /// The steps as the service wants them: blank rows dropped, because an operator adding three
    /// step slots and filling two should get two steps.
    /// </summary>
    public IReadOnlyList<StepInput> ToStepInputs() => Steps
        .Where(step => !step.IsBlank)
        .Select(step => new StepInput(step.TitleFa, step.TitleEn, step.BodyFa, step.BodyEn, step.ClearImage))
        .ToList();
}

public sealed class StepFieldSet
{
    [StringLength(DocumentationStep.TitleMaxLength, ErrorMessage = "validation.tooLong")]
    public string? TitleFa { get; set; }

    [StringLength(DocumentationStep.TitleMaxLength, ErrorMessage = "validation.tooLong")]
    public string? TitleEn { get; set; }

    [StringLength(DocumentationStep.BodyMaxLength, ErrorMessage = "validation.tooLong")]
    public string? BodyFa { get; set; }

    [StringLength(DocumentationStep.BodyMaxLength, ErrorMessage = "validation.tooLong")]
    public string? BodyEn { get; set; }

    public bool ClearImage { get; set; }

    /// <summary>Read-only, for showing the current image. Never posted back as a path.</summary>
    public string? MediaPath { get; set; }

    public bool IsBlank =>
        string.IsNullOrWhiteSpace(TitleFa)
        && string.IsNullOrWhiteSpace(TitleEn)
        && string.IsNullOrWhiteSpace(BodyFa)
        && string.IsNullOrWhiteSpace(BodyEn);
}

public sealed class CategoryEditViewModel
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    [StringLength(ContentSlug.MaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.content.slug")]
    public string? Slug { get; set; }

    [Required(ErrorMessage = "validation.required")]
    [StringLength(DocumentationCategory.TitleMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameFa")]
    public string TitleFa { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(DocumentationCategory.TitleMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameEn")]
    public string TitleEn { get; set; } = string.Empty;

    [Range(0, 10_000, ErrorMessage = "validation.range")]
    [Display(Name = "admin.application.displayOrder")]
    public int DisplayOrder { get; set; } = 100;

    [Display(Name = "admin.application.isEnabled")]
    public bool IsVisible { get; set; } = true;

    public Guid? ConcurrencyToken { get; set; }

    public bool IsNew => Id == Guid.Empty;

    public DocumentationCategorySaveRequest ToRequest() => new(
        Slug, TitleFa, TitleEn, IconName: null, DisplayOrder, IsVisible, ConcurrencyToken);
}

/// <summary>Heading and error block shared by the four content forms.</summary>
public sealed record ContentFormHead(string Title, Guid ProductId);

public sealed class StepImageUploadViewModel
{
    public Guid ArticleId { get; set; }

    public int StepNumber { get; set; }

    [Display(Name = "admin.content.stepImage")]
    public IFormFile? Image { get; set; }
}
