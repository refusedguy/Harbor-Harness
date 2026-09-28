using Harbor.Ui.Framework.Services;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     ISP guard for the theme abstraction (#469). <c>IThemeService</c> used to
///     be one fat interface mixing three roles; it is now the aggregate of
///     <see cref="IThemeReader" /> / <see cref="IThemeApplier" /> /
///     <see cref="IThemeWatcher" />. These tests pin the shape so a future edit
///     cannot silently re-widen a role interface (or re-add a member that only
///     the aggregate exposes).
/// </summary>
public class ThemeRoleInterfaceTests
{
    private static readonly Type[] Roles =
        [typeof(IThemeReader), typeof(IThemeApplier), typeof(IThemeWatcher)];

    /// <summary>Members each role owns — nothing else, see the leak tests.</summary>
    private static readonly Dictionary<Type, string[]> OwnedMembers = new()
    {
        [typeof(IThemeReader)] = ["Current", "IsDark"],
        [typeof(IThemeApplier)] = ["Apply", "ApplyDark", "ApplyLight", "Toggle", "ApplyHds", "SetThemeVariant"],
        [typeof(IThemeWatcher)] = ["LoadJson", "ApplyJson", "Watch"],
    };

    [Test]
    public async Task ThemeService_Aggregates_AllThreeRoles()
    {
        var missing = new List<string>();
        foreach (Type role in Roles)
        {
            if (!role.IsAssignableFrom(typeof(IThemeService)))
            {
                missing.Add(role.Name);
            }
        }

        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task ThemeService_Declares_NoMembers_Of_ItsOwn()
    {
        // Every member now comes from a role interface — an implementation can
        // therefore be handed only the roles it actually honours.
        await Assert.That(typeof(IThemeService).GetMembers().Length).IsEqualTo(0);
    }

    [Test]
    public async Task EveryRole_Declares_ItsOwnMembers()
    {
        var missing = new List<string>();
        foreach ((Type role, string[] members) in OwnedMembers)
        {
            foreach (string member in members)
            {
                if (role.GetMember(member).Length == 0)
                {
                    missing.Add($"{role.Name}.{member}");
                }
            }
        }

        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task Role_Does_Not_Leak_AnotherRolesMembers()
    {
        var leaked = new List<string>();
        foreach (Type role in Roles)
        {
            foreach ((Type owner, string[] members) in OwnedMembers)
            {
                if (owner == role)
                {
                    continue;
                }

                foreach (string member in members)
                {
                    if (role.GetMember(member).Length > 0)
                    {
                        leaked.Add($"{role.Name}.{member}");
                    }
                }
            }
        }

        await Assert.That(leaked).IsEmpty();
    }

    [Test]
    public async Task Roles_Are_Mutually_Independent()
    {
        // Reading the theme must not force an implementation to implement the
        // apply role (the JsonThemeLoader LSP trap) and vice versa.
        var coupled = new List<string>();
        foreach (Type role in Roles)
        {
            foreach (Type other in Roles)
            {
                if (role != other && other.IsAssignableFrom(role))
                {
                    coupled.Add($"{role.Name} -> {other.Name}");
                }
            }
        }

        await Assert.That(coupled).IsEmpty();
    }

    /// <summary>A reader-only fake compiles only because <see cref="IThemeReader" /> is narrow.</summary>
    private sealed class ReaderOnlyFake : IThemeReader
    {
        public string Current => "dark";

        public bool IsDark => true;
    }

    [Test]
    public async Task ReaderOnlyFake_Implements_OnlyTheReaderRole()
    {
        var fake = new ReaderOnlyFake();

        await Assert.That(fake.IsDark).IsTrue();
        await Assert.That(typeof(IThemeApplier).IsAssignableFrom(typeof(ReaderOnlyFake))).IsFalse();
        await Assert.That(typeof(IThemeWatcher).IsAssignableFrom(typeof(ReaderOnlyFake))).IsFalse();
        await Assert.That(typeof(IThemeService).IsAssignableFrom(typeof(ReaderOnlyFake))).IsFalse();
    }
}
