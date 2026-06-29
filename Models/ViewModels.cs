using System.ComponentModel.DataAnnotations;

namespace SingleSignOn.Models;

public class LoginViewModel
{
    [Required(ErrorMessage = "กรุณากรอกชื่อผู้ใช้")]
    [Display(Name = "ชื่อผู้ใช้ / อีเมล")]
    public string Username { get; set; } = "";

    [Required(ErrorMessage = "กรุณากรอกรหัสผ่าน")]
    [DataType(DataType.Password)]
    [Display(Name = "รหัสผ่าน")]
    public string Password { get; set; } = "";

    /// <summary>Keep the login alive across browser/server restarts until explicit Logout.</summary>
    [Display(Name = "จดจำการเข้าสู่ระบบ")]
    public bool RememberMe { get; set; } = true;

    public string? Error { get; set; }
}

public class ForgotPasswordViewModel
{
    [Required(ErrorMessage = "กรุณากรอกอีเมล")]
    [EmailAddress(ErrorMessage = "รูปแบบอีเมลไม่ถูกต้อง")]
    [Display(Name = "อีเมล")]
    public string Email { get; set; } = "";

    public string? Error { get; set; }
}

public class ResetPasswordViewModel
{
    [Required]
    public string Token { get; set; } = "";

    [Required(ErrorMessage = "กรุณากรอกรหัสผ่านใหม่")]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "รหัสผ่านต้องมีอย่างน้อย 6 ตัวอักษร")]
    [DataType(DataType.Password)]
    [Display(Name = "รหัสผ่านใหม่")]
    public string NewPassword { get; set; } = "";

    [DataType(DataType.Password)]
    [Display(Name = "ยืนยันรหัสผ่านใหม่")]
    [Compare(nameof(NewPassword), ErrorMessage = "รหัสผ่านยืนยันไม่ตรงกัน")]
    public string ConfirmPassword { get; set; } = "";

    public string? Error { get; set; }
}

/// <summary>One cookie to write into the user's browser scoped to the parent domain.</summary>
public record CookieToPlant(string Name, string Value);

/// <summary>Result of a server-side Transplant login.</summary>
public record TransplantResult(bool Success, List<CookieToPlant> Cookies, string HomeUrl);

/// <summary>Everything the browser needs to auto-submit a login form to the app itself.</summary>
public class AutoLoginPrep
{
    public string AppName { get; set; } = "";
    public string PostUrl { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = new();
    public List<CookieToPlant> PlantCookies { get; set; } = new();
}
