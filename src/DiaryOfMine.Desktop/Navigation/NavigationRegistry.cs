using System;
using System.Collections.Generic;

namespace DiaryOfMine.Desktop.Navigation;

public sealed class NavigationRegistry
{
    private readonly List<NavigationPageRegistration> _registrations = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    public IReadOnlyList<NavigationPageRegistration> Registrations => _registrations;

    public NavigationRegistry Register(NavigationPageRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!_ids.Add(registration.Id))
            throw new InvalidOperationException($"导航标识“{registration.Id}”已注册。");

        _registrations.Add(registration);
        return this;
    }
}
