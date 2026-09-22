using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Features.Scope.GetEffectiveScope;
using IAMS.Api.Features.Users;
using IAMS.Api.Features.Users.AssignUserLocations;
using IAMS.Api.Features.Users.GetUserLocations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// Handler tests for the new user-Location assignment slices and the reworked <c>/me/scope</c> endpoint:
/// replace-set semantics, cross-Company isolation for Admin callers, invalid-Location validation, and the
/// scope response's Company + assigned-Location shape by role.
/// </summary>
public class UserLocationsHandlerTests
{
    private sealed record World(IamsDbContext Db, Guid CompanyId, Guid TargetUserId, Guid Loc1, Guid Loc2);

    private static World Seed(UserRole targetRole = UserRole.User)
    {
        var db = TestDb.New();
        var companyId = Guid.NewGuid();
        db.Companies.Add(new Company { Id = companyId, Name = "Co" });
        var loc1 = new Location { Id = Guid.NewGuid(), CompanyId = companyId, Name = "L1" };
        var loc2 = new Location { Id = Guid.NewGuid(), CompanyId = companyId, Name = "L2" };
        db.Locations.AddRange(loc1, loc2);
        var target = new User
        {
            Id = Guid.NewGuid(), Username = "target", CompanyId = companyId, Role = targetRole,
            ActivationKeyHash = Guid.NewGuid().ToString("N"), SecurityStamp = "s"
        };
        db.Users.Add(target);
        db.SaveChanges();
        return new World(db, companyId, target.Id, loc1.Id, loc2.Id);
    }

    private static FakeCurrentUser Admin(Guid companyId) =>
        new() { UserId = Guid.NewGuid(), CompanyId = companyId, Role = UserRole.Admin };

    // ── Assign ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Assign_ReplacesTheEntireSet()
    {
        var w = Seed();
        var handler = new AssignUserLocationsHandler(w.Db, Admin(w.CompanyId), new FakeClock(DateTimeOffset.UtcNow));

        // First assign both.
        var first = await handler.HandleAsync(w.TargetUserId, new AssignUserLocationsCommand(new[] { w.Loc1, w.Loc2 }), default);
        Assert.Equal(2, Assert.IsType<Ok<UserLocationsResponse>>(first.Result).Value!.Locations.Count);

        // Re-assign to just one → the other is removed (replace, not merge).
        var second = await handler.HandleAsync(w.TargetUserId, new AssignUserLocationsCommand(new[] { w.Loc2 }), default);
        var loc = Assert.Single(Assert.IsType<Ok<UserLocationsResponse>>(second.Result).Value!.Locations);
        Assert.Equal(w.Loc2, loc.Id);
        Assert.Equal(1, w.Db.UserLocationAssignments.Count());
    }

    [Fact]
    public async Task Assign_EmptyList_ClearsAllAssignments()
    {
        var w = Seed();
        var handler = new AssignUserLocationsHandler(w.Db, Admin(w.CompanyId), new FakeClock(DateTimeOffset.UtcNow));
        await handler.HandleAsync(w.TargetUserId, new AssignUserLocationsCommand(new[] { w.Loc1 }), default);

        var result = await handler.HandleAsync(w.TargetUserId, new AssignUserLocationsCommand(Array.Empty<Guid>()), default);

        Assert.Empty(Assert.IsType<Ok<UserLocationsResponse>>(result.Result).Value!.Locations);
        Assert.Empty(w.Db.UserLocationAssignments);
    }

    [Fact]
    public async Task Assign_LocationInAnotherCompany_Returns400()
    {
        var w = Seed();
        var otherLoc = Guid.NewGuid();
        w.Db.Locations.Add(new Location { Id = otherLoc, CompanyId = Guid.NewGuid(), Name = "Foreign" });
        w.Db.SaveChanges();
        var handler = new AssignUserLocationsHandler(w.Db, Admin(w.CompanyId), new FakeClock(DateTimeOffset.UtcNow));

        var result = await handler.HandleAsync(w.TargetUserId, new AssignUserLocationsCommand(new[] { w.Loc1, otherLoc }), default);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("validation_failed", problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task Assign_AdminFromDifferentCompany_Returns404()
    {
        var w = Seed();
        var foreignAdmin = new FakeCurrentUser { UserId = Guid.NewGuid(), CompanyId = Guid.NewGuid(), Role = UserRole.Admin };
        var handler = new AssignUserLocationsHandler(w.Db, foreignAdmin, new FakeClock(DateTimeOffset.UtcNow));

        var result = await handler.HandleAsync(w.TargetUserId, new AssignUserLocationsCommand(new[] { w.Loc1 }), default);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ProblemHttpResult>(result.Result).StatusCode);
    }

    [Fact]
    public void AssignValidator_RejectsDuplicatesAndEmptyGuids()
    {
        var validator = new AssignUserLocationsValidator();
        var dup = Guid.NewGuid();
        Assert.False(validator.Validate(new AssignUserLocationsCommand(new[] { dup, dup })).IsValid);
        Assert.False(validator.Validate(new AssignUserLocationsCommand(new[] { Guid.Empty })).IsValid);
        Assert.True(validator.Validate(new AssignUserLocationsCommand(new[] { Guid.NewGuid() })).IsValid);
    }

    // ── Get ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ReturnsAssignedLocations()
    {
        var w = Seed();
        await new AssignUserLocationsHandler(w.Db, Admin(w.CompanyId), new FakeClock(DateTimeOffset.UtcNow))
            .HandleAsync(w.TargetUserId, new AssignUserLocationsCommand(new[] { w.Loc1 }), default);

        var result = await new GetUserLocationsHandler(w.Db, Admin(w.CompanyId)).HandleAsync(w.TargetUserId, default);

        var loc = Assert.Single(Assert.IsType<Ok<UserLocationsResponse>>(result.Result).Value!.Locations);
        Assert.Equal(w.Loc1, loc.Id);
    }

    // ── /me/scope ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scope_LocationRestrictedUser_ShowsOnlyAssignedLocations()
    {
        var w = Seed();
        w.Db.UserLocationAssignments.Add(new UserLocationAssignment
        {
            Id = Guid.NewGuid(), UserId = w.TargetUserId, LocationId = w.Loc1
        });
        w.Db.SaveChanges();
        var currentUser = new FakeCurrentUser { UserId = w.TargetUserId, CompanyId = w.CompanyId, Role = UserRole.User };
        var resolver = new IAMS.Api.Common.Access.AccessScopeResolver(w.Db, currentUser);
        var handler = new GetEffectiveScopeHandler(w.Db, currentUser, resolver);

        var response = Assert.IsType<Ok<EffectiveScopeResponse>>((await handler.HandleAsync(default)).Result).Value!;

        Assert.Equal(200, response.Role.Code);
        Assert.False(response.UnrestrictedCompanyAccess);
        Assert.False(response.SystemWideAccess);
        var loc = Assert.Single(response.AssignedLocations);
        Assert.Equal(w.Loc1, loc.Id);
    }

    [Fact]
    public async Task Scope_Admin_ShowsAllCompanyLocations_Unrestricted()
    {
        var w = Seed();
        var currentUser = Admin(w.CompanyId);
        currentUser.UserId = w.TargetUserId; // reuse the seeded user's identity, but as an Admin
        // Make the seeded target an Admin so /me/scope resolves the current user's own record.
        var self = w.Db.Users.Single(u => u.Id == w.TargetUserId);
        self.Role = UserRole.Admin;
        w.Db.SaveChanges();

        var resolver = new IAMS.Api.Common.Access.AccessScopeResolver(w.Db, currentUser);
        var handler = new GetEffectiveScopeHandler(w.Db, currentUser, resolver);

        var response = Assert.IsType<Ok<EffectiveScopeResponse>>((await handler.HandleAsync(default)).Result).Value!;

        Assert.True(response.UnrestrictedCompanyAccess);
        Assert.False(response.SystemWideAccess);
        Assert.Equal(2, response.AssignedLocations.Count); // all Company Locations
    }
}
