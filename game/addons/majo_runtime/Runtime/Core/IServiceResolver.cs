#nullable enable

using System;

namespace Majo.Runtime.Core;

public interface IServiceResolver
{
    TService Get<TService>() where TService : class;

    object Get(Type serviceType);

    bool TryGet<TService>(out TService? service) where TService : class;
}
