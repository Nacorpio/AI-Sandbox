using AISandbox.Application.Abstractions;
using AISandbox.Domain.Catalog.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AISandbox.Infrastructure.Protocols;

/// <summary>
/// Invokers are keyed services named by protocol id. Adding a protocol is one class plus one
/// registration.
/// </summary>
internal sealed class ModelInvokerResolver(IServiceProvider services) : IModelInvokerResolver
{
    public IModelInvoker? For(ProtocolId protocol) => services.GetKeyedService<IModelInvoker>(protocol.Value);
}
