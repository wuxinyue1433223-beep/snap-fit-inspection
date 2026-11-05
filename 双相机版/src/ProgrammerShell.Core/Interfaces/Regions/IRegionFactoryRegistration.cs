using Microsoft.Extensions.DependencyInjection;

namespace ProgrammerShell.Core.Interfaces.Regions;

public interface IRegionFactoryRegistration
{
 void Register(IServiceCollection services);
}
