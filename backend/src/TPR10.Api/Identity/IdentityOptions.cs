namespace TPR10.Api.Identity;

// ขอบเขตข้อมูลสำหรับ Task 1; session/lockout policy จะเพิ่มพร้อม use case ใน Task ถัดไป
public static class IdentityOptions
{
    public const int MinimumPasswordScalars = 15;
    public const int MaximumPasswordScalars = 128;
    public const int MaximumUsernameLength = 128;
    public const int MaximumNormalizedUsernameLength = 256;
}
