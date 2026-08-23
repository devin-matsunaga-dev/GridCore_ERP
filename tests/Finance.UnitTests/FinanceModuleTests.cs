using GridCore.Modules.Finance;
using GridCore.Platform.Modules;

namespace GridCore.Modules.Finance.UnitTests;

public class FinanceModuleTests
{
    [Fact]
    public void Module_declares_a_snake_case_schema_name()
    {
        IModule module = new FinanceModule();

        Assert.False(string.IsNullOrWhiteSpace(module.Name));
        Assert.Matches("^[a-z][a-z0-9_]*$", module.Name);
    }
}
