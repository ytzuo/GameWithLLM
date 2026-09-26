using System;

public abstract class ContentValidationRule : IContentValidationRule
{
    private readonly Action<ContentValidationContext> _validation;

    protected ContentValidationRule(Action<ContentValidationContext> validation)
    {
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
    }

    public abstract string RuleId { get; }
    public abstract string Module { get; }

    public ValidationResult Validate(ContentValidationContext context)
    {
        try
        {
            _validation(context ?? throw new ArgumentNullException(nameof(context)));
            return ValidationResult.Success(this);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(this, ex);
        }
    }
}

public sealed class ProjectConfigurationValidator : ContentValidationRule
{
    public ProjectConfigurationValidator(Action<ContentValidationContext> validation = null)
        : base(validation ?? (_ => AddressablesA1ProjectSetup.Verify())) { }
    public override string RuleId => "ProjectConfiguration";
    public override string Module => "Project";
}

public sealed class ContentOwnershipValidator : ContentValidationRule
{
    public ContentOwnershipValidator(Action<ContentValidationContext> validation = null)
        : base(validation ?? (_ => AddressablesA0InventoryValidator.Verify())) { }
    public override string RuleId => "OwnershipInventory";
    public override string Module => "Ownership";
}

public sealed class ContentCatalogValidator : ContentValidationRule
{
    public ContentCatalogValidator(Action<ContentValidationContext> validation = null)
        : base(validation ?? (_ => AddressablesA2ProjectSetup.Verify())) { }
    public override string RuleId => "ReleaseArtifacts";
    public override string Module => "Catalogs";
}

public sealed class ToolPackageValidator : ContentValidationRule
{
    public ToolPackageValidator(Action<ContentValidationContext> validation = null)
        : base(validation ?? (_ => ToolPackageReleaseGate.ValidateCandidate())) { }
    public override string RuleId => "ToolPackageGate";
    public override string Module => "ToolPackages";
}

public sealed class UiContentValidator : ContentValidationRule
{
    public UiContentValidator(Action<ContentValidationContext> validation = null)
        : base(validation ?? (_ => AddressablesA3ProjectSetup.Verify())) { }
    public override string RuleId => "UiContracts";
    public override string Module => "UI";
}

public sealed class ItemContentValidator : ContentValidationRule
{
    public ItemContentValidator(Action<ContentValidationContext> validation = null)
        : base(validation ?? (_ => AddressablesA4ProjectSetup.Verify())) { }
    public override string RuleId => "ItemCatalog";
    public override string Module => "Items";
}

public sealed class CharacterContentValidator : ContentValidationRule
{
    public CharacterContentValidator(Action<ContentValidationContext> validation = null)
        : base(validation ?? (_ => AddressablesA5ProjectSetup.Verify())) { }
    public override string RuleId => "CharacterContent";
    public override string Module => "Characters";
}

public sealed class SceneContentValidator : ContentValidationRule
{
    public SceneContentValidator(Action<ContentValidationContext> validation = null)
        : base(validation ?? (_ => AddressablesA6ProjectSetup.Verify())) { }
    public override string RuleId => "SceneBoundaries";
    public override string Module => "Scenes";
}

public sealed class ReleaseBudgetValidator : ContentValidationRule
{
    public ReleaseBudgetValidator(Action<ContentValidationContext> validation = null)
        : base(validation ?? (_ => ContentReleasePipeline.ValidateReleaseInputs())) { }
    public override string RuleId => "ReleaseInputs";
    public override string Module => "Release";
}
