using System.Reflection;
using System.Runtime.Versioning;
using varManager.Ui;
using Xunit;

namespace varManager.App.Tests
{
    public class AppProjectTests
    {
        [Fact]
        public void App_targets_net10()
        {
            var target = typeof(MainWindow).Assembly.GetCustomAttribute<TargetFrameworkAttribute>();

            Assert.Equal(".NETCoreApp,Version=v10.0", target?.FrameworkName);
        }
    }
}
