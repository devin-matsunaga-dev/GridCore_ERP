using GridCore.Platform.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Platform.UnitTests.Modules;

public class ModuleRegistrationTests
{
    private static IConfiguration EmptyConfiguration => new ConfigurationBuilder().Build();

    [Fact]
    public void AddModules_registers_every_module_and_calls_its_service_hook()
    {
        var services = new ServiceCollection();
        var customers = new FakeModule("customers");
        var billing = new FakeModule("billing");

        var registered = services.AddModules(EmptyConfiguration, customers, billing);

        Assert.Equal(new[] { "customers", "billing" }, registered.Select(m => m.Name));
        Assert.True(customers.ServicesAdded);
        Assert.True(billing.ServicesAdded);

        var resolved = services.BuildServiceProvider().GetServices<IModule>().ToList();
        Assert.Equal(2, resolved.Count);
    }

    [Fact]
    public void AddModules_rejects_two_modules_claiming_the_same_schema()
    {
        var services = new ServiceCollection();

        var act = () => services.AddModules(EmptyConfiguration, new FakeModule("billing"), new FakeModule("billing"));

        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("billing", ex.Message, StringComparison.Ordinal);
    }

    private sealed class FakeModule(string name) : IModule
    {
        public string Name { get; } = name;

        public bool ServicesAdded { get; private set; }

        public void AddServices(IServiceCollection services, IConfiguration configuration) => ServicesAdded = true;

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }
    }
}
