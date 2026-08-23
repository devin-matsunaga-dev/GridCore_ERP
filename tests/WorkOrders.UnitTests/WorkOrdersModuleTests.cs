using GridCore.Modules.WorkOrders;
using GridCore.Platform.Modules;

namespace GridCore.Modules.WorkOrders.UnitTests;

public class WorkOrdersModuleTests
{
    [Fact]
    public void Module_declares_a_snake_case_schema_name()
    {
        IModule module = new WorkOrdersModule();

        Assert.False(string.IsNullOrWhiteSpace(module.Name));
        Assert.Matches("^[a-z][a-z0-9_]*$", module.Name);
    }
}
