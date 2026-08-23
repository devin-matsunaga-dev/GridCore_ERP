using GridCore.Modules.Customers;
using GridCore.Platform.Modules;

namespace GridCore.Modules.Customers.UnitTests;

public class CustomersModuleTests
{
    [Fact]
    public void Module_declares_a_snake_case_schema_name()
    {
        IModule module = new CustomersModule();

        Assert.False(string.IsNullOrWhiteSpace(module.Name));
        Assert.Matches("^[a-z][a-z0-9_]*$", module.Name);
    }
}
