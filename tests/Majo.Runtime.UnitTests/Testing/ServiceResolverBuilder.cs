using Majo.Runtime.Core;

namespace Majo.Runtime.UnitTests;

internal sealed class ServiceResolverBuilder
{
    private readonly Dictionary<Type, object> services = new();

    public ServiceResolverBuilder Add<TService>(TService instance) where TService : class
    {
        services[typeof(TService)] = instance;
        return this;
    }

    public IServiceResolver Build() => new SimpleServiceResolver(services);

    private sealed class SimpleServiceResolver : IServiceResolver
    {
        private readonly Dictionary<Type, object> services;

        public SimpleServiceResolver(Dictionary<Type, object> services)
        {
            this.services = services;
        }

        public TService Resolve<TService>() where TService : class
        {
            if (TryGet<TService>(out var service) && service is not null)
            {
                return service;
            }

            throw new InvalidOperationException($"Service '{typeof(TService).FullName}' not found.");
        }

        public object Resolve(Type serviceType)
        {
            if (services.TryGetValue(serviceType, out var obj))
            {
                return obj;
            }

            throw new InvalidOperationException($"Service '{serviceType.FullName}' not found.");
        }

        public bool TryGet<TService>(out TService? service) where TService : class
        {
            if (services.TryGetValue(typeof(TService), out var obj) && obj is TService typed)
            {
                service = typed;
                return true;
            }

            service = null;
            return false;
        }
    }
}
