using System.Globalization;
using System.Text.Json;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Policies;

namespace TPR10.Api.IntegrationTests;

public sealed class FieldAdvanceRulesTests
{
    [Theory]
    [InlineData("0.01", new[] { "supervisor", "project-manager", "L1" })]
    [InlineData("999.99", new[] { "supervisor", "project-manager", "L1" })]
    [InlineData("1000.00", new[] { "supervisor", "project-manager", "L1" })]
    [InlineData("1000.000", new[] { "supervisor", "project-manager", "L1" })]
    [InlineData("1000.01", new[] { "supervisor", "project-manager", "L1", "L2" })]
    [InlineData("4999.99", new[] { "supervisor", "project-manager", "L1", "L2" })]
    [InlineData("5000.00", new[] { "supervisor", "project-manager", "L1", "L2" })]
    public void Evaluation_includes_upper_boundary_and_accumulates_every_required_level(
        string amount, string[] expected)
    {
        var result = ApprovalLevelEvaluator.Evaluate(Money(amount), Snapshot(Content()));

        Assert.Equal(200, result.Status);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("-1000")]
    [InlineData("0.001")]
    [InlineData("1000.001")]
    [InlineData("1000.005")]
    [InlineData("5000.001")]
    public void Evaluation_rejects_nonpositive_or_subsatang_total_without_rounding(string amount)
    {
        var result = ApprovalLevelEvaluator.Evaluate(Money(amount), Snapshot(Content()));

        AssertFailure(result, "invalid-total");
    }

    [Theory]
    [InlineData("5000.01")]
    [InlineData("79228162514264337593543950335")]
    public void Evaluation_fails_when_total_exceeds_all_configured_authority(string amount)
    {
        var result = ApprovalLevelEvaluator.Evaluate(Money(amount), Snapshot(Content()));

        AssertFailure(result, "unsupported-total");
    }

    [Fact]
    public void Evaluation_uses_policy_data_for_any_number_of_levels()
    {
        var content = Content() with
        {
            Bands = [new(1, "A", 0.01m), new(2, "B", 0.02m), new(3, "C", 9999999999999999.99m)]
        };

        var result = ApprovalLevelEvaluator.Evaluate(9999999999999999.99m, Snapshot(content));

        Assert.Equal(200, result.Status);
        Assert.Equal(new[] { "supervisor", "project-manager", "A", "B", "C" }, result.Value);
    }

    [Fact]
    public void Validation_accepts_the_five_initial_category_names_without_standard_rates()
    {
        Assert.Empty(PolicyValidation.Validate(Content()));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("12.3400")]
    [InlineData("9999999999999999.99")]
    public void Validation_accepts_nonnegative_satang_standard_rates_with_a_unit(string rate)
    {
        var content = WithCategory(Content().Categories[0] with { StandardRate = Money(rate), Unit = "เที่ยว" });

        Assert.Empty(PolicyValidation.Validate(content));
    }

    [Fact]
    public void Validation_allows_added_and_disabled_categories_without_requiring_a_standard_rate()
    {
        var content = Content();
        content = content with
        {
            ClearingDays = 1,
            Categories = [.. content.Categories.Select(c => c with { Active = false }),
                new(Guid.NewGuid(), "หมวดเพิ่มเติมสำหรับทดสอบ", true, "ครั้ง", null, ["ใบรับเงิน"])]
        };

        Assert.Empty(PolicyValidation.Validate(content));
    }

    [Fact]
    public void Validation_accepts_text_at_the_existing_storage_limits()
    {
        var content = WithCategory(Content().Categories[0] with
        {
            Name = new string('ก', 200),
            Unit = new string('ข', 100),
            StandardRate = 1m
        });
        content = content with { Bands = [new(1, new string('L', 64), 1000m)] };

        Assert.Empty(PolicyValidation.Validate(content));
    }

    [Theory]
    [MemberData(nameof(InvalidContentCases))]
    public void Validation_rejects_malformed_policy_content(string scenario)
    {
        var errors = PolicyValidation.Validate(InvalidContent(scenario));

        Assert.NotEmpty(errors);
        Assert.All(errors, error => Assert.False(string.IsNullOrWhiteSpace(error)));
    }

    [Theory]
    [MemberData(nameof(InvalidContentCases))]
    public void Evaluation_validates_the_entire_snapshot_before_returning_any_authority(string scenario)
    {
        // Even defects in a later band or an inactive category must fail closed at the first band.
        var result = ApprovalLevelEvaluator.Evaluate(0.01m, Snapshot(InvalidContent(scenario)));

        AssertFailure(result, "invalid-policy");
    }

    [Theory]
    [InlineData("null-snapshot")]
    [InlineData("empty-company-id")]
    [InlineData("empty-project-id")]
    [InlineData("null-hash")]
    [InlineData("empty-hash")]
    [InlineData("blank-hash")]
    [InlineData("short-hash")]
    [InlineData("long-hash")]
    [InlineData("nonhex-hash")]
    [InlineData("uppercase-hash")]
    public void Evaluation_rejects_missing_snapshot_identity(string scenario)
    {
        var snapshot = Snapshot(Content());
        snapshot = scenario switch
        {
            "null-snapshot" => null!,
            "empty-company-id" => snapshot with { CompanyVersionId = Guid.Empty },
            "empty-project-id" => snapshot with { ProjectVersionId = Guid.Empty },
            "null-hash" => snapshot with { ContentHash = null! },
            "empty-hash" => snapshot with { ContentHash = "" },
            "blank-hash" => snapshot with { ContentHash = " " },
            "short-hash" => snapshot with { ContentHash = new string('a', 63) },
            "long-hash" => snapshot with { ContentHash = new string('a', 65) },
            "nonhex-hash" => snapshot with { ContentHash = new string('g', 64) },
            "uppercase-hash" => snapshot with { ContentHash = new string('A', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        AssertFailure(ApprovalLevelEvaluator.Evaluate(1000m, snapshot), "invalid-policy");
    }

    [Fact]
    public void Evaluation_accepts_a_project_snapshot()
    {
        var snapshot = Snapshot(Content()) with { ProjectVersionId = Guid.NewGuid() };

        var result = ApprovalLevelEvaluator.Evaluate(1000.01m, snapshot);

        Assert.Equal(200, result.Status);
        Assert.Equal(new[] { "supervisor", "project-manager", "L1", "L2" }, result.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rules_leave_snapshot_content_unchanged_on_success_and_failure(bool malformed)
    {
        var content = malformed ? InvalidContent("reversed-bands") : Content();
        var snapshot = Snapshot(content);
        var before = JsonSerializer.Serialize(snapshot);

        PolicyValidation.Validate(content);
        ApprovalLevelEvaluator.Evaluate(1000.01m, snapshot);

        Assert.Equal(before, JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public void Evaluation_returns_independent_results_without_mutating_policy_levels()
    {
        var snapshot = Snapshot(Content());
        var first = ApprovalLevelEvaluator.Evaluate(1000.01m, snapshot);
        Assert.Equal(200, first.Status);
        Assert.NotNull(first.Value);
        first.Value[0] = "changed-by-caller";
        first.Value[2] = "changed-level";

        var second = ApprovalLevelEvaluator.Evaluate(1000.01m, snapshot);

        Assert.Equal(200, second.Status);
        Assert.Equal(new[] { "supervisor", "project-manager", "L1", "L2" }, second.Value);
        Assert.Equal("L1", snapshot.Content.Bands[0].LevelCode);
    }

    public static TheoryData<string> InvalidContentCases => new()
    {
        "null-content", "zero-days", "negative-days", "null-bands", "empty-bands", "null-band",
        "zero-upper", "negative-upper", "subsatang-upper", "equal-upper", "decreasing-upper",
        "reversed-bands", "zero-order", "negative-order", "starting-order-gap", "order-gap", "duplicate-order",
        "null-level", "empty-level", "blank-level", "padded-level", "duplicate-level", "case-duplicate-level",
        "supervisor-level", "project-manager-level", "null-categories", "empty-categories", "null-category",
        "empty-category-id", "duplicate-category-id", "null-category-name", "blank-category-name",
        "negative-rate", "subsatang-rate", "rate-without-unit", "rate-empty-unit", "rate-blank-unit",
        "null-evidence", "empty-evidence", "null-evidence-type", "blank-evidence-type", "inactive-without-evidence",
        "overflow-upper", "overflow-rate", "long-level", "long-category-name", "long-unit"
    };

    private static PolicyContent InvalidContent(string scenario)
    {
        var content = Content();
        var firstBand = content.Bands[0];
        var lastBand = content.Bands[1];
        var category = content.Categories[0];
        return scenario switch
        {
            "null-content" => null!,
            "zero-days" => content with { ClearingDays = 0 },
            "negative-days" => content with { ClearingDays = -1 },
            "null-bands" => content with { Bands = null! },
            "empty-bands" => content with { Bands = [] },
            "null-band" => content with { Bands = [firstBand, null!] },
            "zero-upper" => content with { Bands = [firstBand with { UpperInclusive = 0m }] },
            "negative-upper" => content with { Bands = [firstBand with { UpperInclusive = -1m }] },
            "subsatang-upper" => content with { Bands = [firstBand, lastBand with { UpperInclusive = 5000.001m }] },
            "overflow-upper" => content with { Bands = [firstBand, lastBand with { UpperInclusive = 10000000000000000m }] },
            "equal-upper" => content with { Bands = [firstBand, lastBand with { UpperInclusive = 1000m }] },
            "decreasing-upper" => content with { Bands = [firstBand, lastBand with { UpperInclusive = 999.99m }] },
            "reversed-bands" => content with { Bands = [lastBand, firstBand] },
            "zero-order" => content with { Bands = [firstBand with { Order = 0 }, lastBand] },
            "negative-order" => content with { Bands = [firstBand with { Order = -1 }, lastBand] },
            "starting-order-gap" => content with { Bands = [firstBand with { Order = 2 }, lastBand with { Order = 3 }] },
            "order-gap" => content with { Bands = [firstBand, lastBand with { Order = 3 }] },
            "duplicate-order" => content with { Bands = [firstBand, lastBand with { Order = 1 }] },
            "null-level" => content with { Bands = [firstBand, lastBand with { LevelCode = null! }] },
            "empty-level" => content with { Bands = [firstBand, lastBand with { LevelCode = "" }] },
            "blank-level" => content with { Bands = [firstBand, lastBand with { LevelCode = " " }] },
            "padded-level" => content with { Bands = [firstBand, lastBand with { LevelCode = " L2 " }] },
            "long-level" => content with { Bands = [firstBand, lastBand with { LevelCode = new string('L', 65) }] },
            "duplicate-level" => content with { Bands = [firstBand, lastBand with { LevelCode = "L1" }] },
            "case-duplicate-level" => content with { Bands = [firstBand, lastBand with { LevelCode = "l1" }] },
            "supervisor-level" => content with { Bands = [firstBand, lastBand with { LevelCode = "SUPERVISOR" }] },
            "project-manager-level" => content with { Bands = [firstBand, lastBand with { LevelCode = "project-manager" }] },
            "null-categories" => content with { Categories = null! },
            "empty-categories" => content with { Categories = [] },
            "null-category" => content with { Categories = [category, null!] },
            "empty-category-id" => WithCategory(category with { Id = Guid.Empty }),
            "duplicate-category-id" => content with { Categories = [category, content.Categories[1] with { Id = category.Id }] },
            "null-category-name" => WithCategory(category with { Name = null! }),
            "blank-category-name" => WithCategory(category with { Name = " " }),
            "long-category-name" => WithCategory(category with { Name = new string('ก', 201) }),
            "long-unit" => WithCategory(category with { Unit = new string('ข', 101) }),
            "negative-rate" => WithCategory(category with { Unit = "เที่ยว", StandardRate = -0.01m }),
            "subsatang-rate" => WithCategory(category with { Unit = "เที่ยว", StandardRate = 12.345m }),
            "overflow-rate" => WithCategory(category with { Unit = "เที่ยว", StandardRate = 10000000000000000m }),
            "rate-without-unit" => WithCategory(category with { StandardRate = 1m }),
            "rate-empty-unit" => WithCategory(category with { Unit = "", StandardRate = 1m }),
            "rate-blank-unit" => WithCategory(category with { Unit = " ", StandardRate = 1m }),
            "null-evidence" => WithCategory(category with { EvidenceTypes = null! }),
            "empty-evidence" => WithCategory(category with { EvidenceTypes = [] }),
            "null-evidence-type" => WithCategory(category with { EvidenceTypes = ["ใบเสร็จ", null!] }),
            "blank-evidence-type" => WithCategory(category with { EvidenceTypes = ["ใบเสร็จ", " "] }),
            "inactive-without-evidence" => WithCategory(category with { Active = false, EvidenceTypes = [] }),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    // These bands, days and rates are synthetic test data, never business defaults or seeds.
    private static PolicyContent Content() => new(7,
        [new(1, "L1", 1000.00m), new(2, "L2", 5000.00m)],
        [.. new[] { "เดินทาง", "ที่พัก", "อาหาร", "เบี้ยเลี้ยง", "น้ำมัน" }
            .Select(name => new CategoryRule(Guid.NewGuid(), name, true, null, null, ["ใบเสร็จ"]))]);

    private static PolicyContent WithCategory(CategoryRule category) => Content() with { Categories = [category] };

    private static PolicySnapshot Snapshot(PolicyContent content) => new(Guid.NewGuid(), null, content, new string('d', 64));

    private static decimal Money(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static void AssertFailure(FinanceResult<string[]> result, string code)
    {
        Assert.Equal(400, result.Status);
        Assert.Equal(code, result.Code);
        Assert.Null(result.Value);
    }
}
