using Microsoft.Extensions.DependencyInjection;
using ProgrammerShell.Features.Flasher.ViewModels;
using ProgrammerShell.Features.Flasher.Views;

namespace ProgrammerShell.Features.Flasher;

public static class FlasherFeatureServiceCollectionExtensions
{
 public static IServiceCollection AddFlasherFeature(this IServiceCollection services)
 {
 services.AddSingleton<FlasherMainViewModel>();
 services.AddSingleton<FlasherMainView>();
 return services;
 }
}
