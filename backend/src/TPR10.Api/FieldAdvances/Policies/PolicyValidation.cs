namespace TPR10.Api.FieldAdvances.Policies;

public static class PolicyValidation
{
    // Storage limit from Task 1's numeric(18,2), not a business approval threshold.
    private const decimal MaximumPolicyAmount = 9999999999999999.99m;

    public static string[] Validate(PolicyContent content)
    {
        if (content is null) return ["ต้องระบุเนื้อหากติกา"];

        var errors = new List<string>();
        if (content.ClearingDays <= 0) errors.Add("จำนวนวันเคลียร์ต้องเป็นจำนวนเต็มบวก");

        if (content.Bands is not { Length: > 0 })
        {
            errors.Add("ต้องระบุระดับวงเงินอนุมัติ");
        }
        else
        {
            var levels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "supervisor", "project-manager" };
            decimal previousUpper = 0m;
            for (var index = 0; index < content.Bands.Length; index++)
            {
                var band = content.Bands[index];
                if (band is null)
                {
                    errors.Add($"ระดับวงเงินลำดับที่ {index + 1} ต้องมีข้อมูล");
                    continue;
                }

                if (band.Order != index + 1)
                    errors.Add($"ระดับวงเงินลำดับที่ {index + 1} ต้องเรียงต่อเนื่องจาก 1");
                if (string.IsNullOrWhiteSpace(band.LevelCode) || band.LevelCode.Length > 64 || band.LevelCode != band.LevelCode.Trim()
                    || !levels.Add(band.LevelCode))
                    errors.Add($"รหัสระดับวงเงินลำดับที่ {index + 1} ต้องไม่ว่าง ไม่ซ้ำ ไม่มีช่องว่างหัวท้าย และยาวไม่เกิน 64 ตัวอักษร");
                if (band.UpperInclusive <= 0m || band.UpperInclusive <= previousUpper
                    || band.UpperInclusive > MaximumPolicyAmount || !IsMoney(band.UpperInclusive))
                    errors.Add($"ขอบบนวงเงินลำดับที่ {index + 1} ต้องเป็นจำนวนบวกที่เพิ่มขึ้น เป็นบาทและสตางค์ และไม่เกินขนาดที่ระบบรองรับ");
                previousUpper = band.UpperInclusive;
            }
        }

        if (content.Categories is not { Length: > 0 })
        {
            errors.Add("ต้องระบุหมวดค่าใช้จ่าย");
        }
        else
        {
            var categoryIds = new HashSet<Guid>();
            for (var index = 0; index < content.Categories.Length; index++)
            {
                var category = content.Categories[index];
                if (category is null)
                {
                    errors.Add($"หมวดค่าใช้จ่ายลำดับที่ {index + 1} ต้องมีข้อมูล");
                    continue;
                }

                if (category.Id == Guid.Empty || !categoryIds.Add(category.Id))
                    errors.Add($"รหัสหมวดค่าใช้จ่ายลำดับที่ {index + 1} ต้องไม่ว่างและไม่ซ้ำ");
                if (string.IsNullOrWhiteSpace(category.Name) || category.Name.Length > 200)
                    errors.Add($"หมวดค่าใช้จ่ายลำดับที่ {index + 1} ต้องมีชื่อยาวไม่เกิน 200 ตัวอักษร");
                if (category.Unit is { Length: > 100 })
                    errors.Add($"หน่วยหมวดค่าใช้จ่ายลำดับที่ {index + 1} ต้องยาวไม่เกิน 100 ตัวอักษร");
                if (category.StandardRate is { } rate)
                {
                    if (rate < 0m || rate > MaximumPolicyAmount || !IsMoney(rate))
                        errors.Add($"อัตรามาตรฐานหมวดลำดับที่ {index + 1} ต้องไม่ติดลบ เป็นบาทและสตางค์ และไม่เกินขนาดที่ระบบรองรับ");
                    if (string.IsNullOrWhiteSpace(category.Unit))
                        errors.Add($"อัตรามาตรฐานหมวดลำดับที่ {index + 1} ต้องมีหน่วย");
                }

                if (category.EvidenceTypes is not { Length: > 0 }
                    || category.EvidenceTypes.Any(string.IsNullOrWhiteSpace))
                    errors.Add($"หมวดค่าใช้จ่ายลำดับที่ {index + 1} ต้องมีประเภทหลักฐานที่ไม่ว่าง");
            }
        }

        return errors.ToArray();
    }

    // Compare only: never replace an input with a rounded amount. Trailing zeroes remain valid.
    internal static bool IsMoney(decimal amount) => decimal.Round(amount, 2) == amount;
}
