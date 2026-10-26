using System;
using Xunit;
using ContextBar.Abstractions;

namespace ContextBar.Abstractions.Tests;

public class ActionParameterDefinitionTests
{
    [Fact]
    public void VisibleWhen_SetsPropertiesCorrectly()
    {
        var param = ActionParameterDefinition.Text("Param1", "Title1", "").VisibleWhen("Mode", "someValue");

        Assert.Equal("Mode", param.VisibleWhenParameter);
        Assert.Equal("someValue", param.VisibleWhenValue);
    }
}
