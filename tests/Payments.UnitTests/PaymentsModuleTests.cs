using GridCore.Modules.Payments;
using GridCore.Platform.Modules;

namespace GridCore.Modules.Payments.UnitTests;

public class PaymentsModuleTests
{
    [Fact]
    public void Module_declares_a_snake_case_schema_name()
    {
        IModule module = new PaymentsModule();

        Assert.False(string.IsNullOrWhiteSpace(module.Name));
        Assert.Matches("^[a-z][a-z0-9_]*$", module.Name);
    }
}
