#nullable enable

using System;

namespace Majo.Runtime.Core
{
    public interface IServiceResolver
    {
        TService Resolve<TService>() where TService : class;

        object Resolve(Type serviceType);

        bool TryGet<TService>(out TService? service) where TService : class;
    }
}

namespace Majo.Runtime
{
    using Majo.Runtime.Core;

    public static class ServiceResolverExtensions
    {
        public static TService Get<TService>(this IServiceResolver resolver) where TService : class
        {
            ArgumentNullException.ThrowIfNull(resolver);
            return resolver.Resolve<TService>();
        }

        public static object Get(this IServiceResolver resolver, Type serviceType)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            return resolver.Resolve(serviceType);
        }
    }
}
