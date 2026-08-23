using GridCore.Modules.Metering;
using GridCore.Platform.Modules;

namespace GridCore.Modules.Metering.UnitTests;

public class MeteringModuleTests
{
    [Fact]
    public void Module_declares_a_snake_case_schema_name()
    {
        IModule module = new MeteringModule();

        Assert.False(string.IsNullOrWhiteSpace(module.Name));
        Assert.Matches("^[a-z][a-z0-9_]*$", module.Name);
    }
}
