using GridCore.Modules.Billing;
using GridCore.Platform.Modules;

namespace GridCore.Modules.Billing.UnitTests;

public class BillingModuleTests
{
    [Fact]
    public void Module_declares_a_snake_case_schema_name()
    {
        IModule module = new BillingModule();

        Assert.False(string.IsNullOrWhiteSpace(module.Name));
        Assert.Matches("^[a-z][a-z0-9_]*$", module.Name);
    }
}
