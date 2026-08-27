using FluentValidation.TestHelper;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Branding;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.DTOs.Demo;
using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Application.DTOs.Project;
using Pointer.Application.DTOs.Role;
using Pointer.Application.DTOs.Status;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.DTOs.User;
using Pointer.Application.Validators;
using Pointer.Domain.Enums;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Covers validators added to close gaps found while auditing every write endpoint for
/// required/length/format coverage (the CreateProjectValidator fix's follow-up).
/// </summary>
public class NewValidatorsTests
{
    [Fact]
    public void EditComment_rejects_empty_body()
    {
        var r = new EditCommentValidator().TestValidate(new EditCommentRequest { Body = "" });
        r.ShouldHaveValidationErrorFor(x => x.Body);
    }

    [Fact]
    public void UpdateCommentStatus_rejects_outofrange_enum()
    {
        var r = new UpdateCommentStatusValidator().TestValidate(
            new UpdateCommentStatusRequest { Status = (CommentStatus)999 });
        r.ShouldHaveValidationErrorFor(x => x.Status);
    }

    [Fact]
    public void UpdateCommentStatus_accepts_valid_enum()
    {
        var r = new UpdateCommentStatusValidator().TestValidate(
            new UpdateCommentStatusRequest { Status = CommentStatus.Archived });
        r.ShouldNotHaveValidationErrorFor(x => x.Status);
    }

    [Fact]
    public void UpdatePredefinedAction_rejects_blank_text_when_provided()
    {
        var r = new UpdatePredefinedActionValidator().TestValidate(
            new UpdatePredefinedActionRequest { Text = "" });
        r.ShouldHaveValidationErrorFor(x => x.Text);
    }

    [Fact]
    public void UpdatePredefinedAction_allows_omitted_fields()
    {
        var r = new UpdatePredefinedActionValidator().TestValidate(new UpdatePredefinedActionRequest());
        r.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    public void CreateTenant_rejects_bad_email(string email)
    {
        var r = new CreateTenantValidator().TestValidate(
            new CreateTenantRequest { Email = email, Password = "password123", DisplayName = "N" });
        r.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void CreateTenant_rejects_weak_password()
    {
        var r = new CreateTenantValidator().TestValidate(
            new CreateTenantRequest { Email = "a@b.com", Password = "short", DisplayName = "N" });
        r.ShouldHaveValidationErrorFor(x => x.Password);
    }

    [Fact]
    public void Register_rejects_missing_projectkey_but_allows_mixed_case()
    {
        var empty = new RegisterValidator().TestValidate(new RegisterRequest
        { Email = "a@b.com", Password = "password123", DisplayName = "N", RoleId = 1, ProjectKey = "" });
        empty.ShouldHaveValidationErrorFor(x => x.ProjectKey);

        // Case is NOT rejected here — AuthService.RegisterAsync lowercases before matching an
        // EXISTING project, unlike CreateProjectValidator which mints a brand new key.
        var mixedCase = new RegisterValidator().TestValidate(new RegisterRequest
        { Email = "a@b.com", Password = "password123", DisplayName = "N", RoleId = 1, ProjectKey = "MyProject" });
        mixedCase.ShouldNotHaveValidationErrorFor(x => x.ProjectKey);
    }

    [Fact]
    public void ForgotPassword_rejects_bad_email()
    {
        var r = new ForgotPasswordValidator().TestValidate(new ForgotPasswordRequest { Email = "nope" });
        r.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void ResetPassword_rejects_missing_token_and_weak_password()
    {
        var r = new ResetPasswordValidator().TestValidate(
            new ResetPasswordRequest { Token = "", NewPassword = "short" });
        r.ShouldHaveValidationErrorFor(x => x.Token);
        r.ShouldHaveValidationErrorFor(x => x.NewPassword);
    }

    [Fact]
    public void RegisterAdmin_rejects_weak_password()
    {
        var r = new RegisterAdminValidator().TestValidate(
            new RegisterAdminRequest { Email = "a@b.com", Password = "short", DisplayName = "N" });
        r.ShouldHaveValidationErrorFor(x => x.Password);
    }

    [Fact]
    public void UpdateRole_rejects_blank_name_when_provided_but_allows_omitted()
    {
        var blank = new UpdateRoleValidator().TestValidate(new UpdateRoleRequest { Name = "" });
        blank.ShouldHaveValidationErrorFor(x => x.Name);

        var omitted = new UpdateRoleValidator().TestValidate(new UpdateRoleRequest());
        omitted.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void UpdateProject_rejects_blank_name_when_provided_but_allows_omitted()
    {
        var blank = new UpdateProjectValidator().TestValidate(new UpdateProjectRequest { Name = "" });
        blank.ShouldHaveValidationErrorFor(x => x.Name);

        var omitted = new UpdateProjectValidator().TestValidate(new UpdateProjectRequest());
        omitted.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void UpdateUser_rejects_weak_password_when_provided_but_allows_omitted()
    {
        var weak = new UpdateUserValidator().TestValidate(new UpdateUserRequest { Password = "short" });
        weak.ShouldHaveValidationErrorFor(x => x.Password);

        var omitted = new UpdateUserValidator().TestValidate(new UpdateUserRequest());
        omitted.ShouldNotHaveValidationErrorFor(x => x.Password);
    }

    [Theory]
    [InlineData("blue")] // not hex
    [InlineData("#zzz")] // invalid hex chars
    public void UpdateStatusPresentation_rejects_bad_color(string color)
    {
        var r = new UpdateStatusPresentationValidator().TestValidate(
            new UpdateStatusPresentationRequest { Color = color });
        r.ShouldHaveValidationErrorFor(x => x.Color);
    }

    [Fact]
    public void UpdateStatusPresentation_accepts_valid_hex_and_omitted_fields()
    {
        var r = new UpdateStatusPresentationValidator().TestValidate(
            new UpdateStatusPresentationRequest { Color = "#0ea5e9" });
        r.ShouldNotHaveValidationErrorFor(x => x.Color);

        var omitted = new UpdateStatusPresentationValidator().TestValidate(new UpdateStatusPresentationRequest());
        omitted.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void UpdateStatusPresentation_rejects_negative_order()
    {
        var r = new UpdateStatusPresentationValidator().TestValidate(
            new UpdateStatusPresentationRequest { Order = -1 });
        r.ShouldHaveValidationErrorFor(x => x.Order);
    }

    [Fact]
    public void DemoRequest_rejects_bad_email()
    {
        var r = new DemoRequestValidator().TestValidate(new DemoRequest { Email = "nope" });
        r.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void BrandingWriteDto_rejects_bad_hex_color()
    {
        var r = new BrandingWriteDtoValidator().TestValidate(new BrandingWriteDto { PrimaryColor = "blue" });
        r.ShouldHaveValidationErrorFor(x => x.PrimaryColor);
    }

    [Fact]
    public void BrandingWriteDto_rejects_non_http_url()
    {
        var r = new BrandingWriteDtoValidator().TestValidate(
            new BrandingWriteDto { Urls = new BrandingUrlsWriteDto { App = "not a url" } });
        Assert.False(r.IsValid);
    }

    [Fact]
    public void BrandingWriteDto_accepts_valid_http_url_and_omitted_fields()
    {
        var r = new BrandingWriteDtoValidator().TestValidate(
            new BrandingWriteDto { Urls = new BrandingUrlsWriteDto { App = "https://example.com" } });
        Assert.True(r.IsValid);

        var omitted = new BrandingWriteDtoValidator().TestValidate(new BrandingWriteDto());
        Assert.True(omitted.IsValid);
    }

    // Regression guard: a super admin's direct-add request omits RoleId entirely (the server
    // forces Deputy server-side once TargetOwnerId is validated) — the auto-validation pipeline
    // must not reject it before UserService.CreateAsync's own TargetOwnerId branch ever runs.
    [Fact]
    public void CreateUser_WithTargetOwnerId_DoesNotRequireRoleId()
    {
        var r = new CreateUserValidator().TestValidate(new CreateUserRequest
        {
            Email = "deputy@acme.test", Password = "password123", DisplayName = "Deputy",
            TargetOwnerId = Guid.NewGuid()
        });
        r.ShouldNotHaveValidationErrorFor(x => x.RoleId);
    }

    [Fact]
    public void CreateUser_WithoutTargetOwnerId_RequiresRoleId()
    {
        var r = new CreateUserValidator().TestValidate(new CreateUserRequest
        { Email = "member@acme.test", Password = "password123", DisplayName = "Member", RoleId = 0 });
        r.ShouldHaveValidationErrorFor(x => x.RoleId);
    }
}
