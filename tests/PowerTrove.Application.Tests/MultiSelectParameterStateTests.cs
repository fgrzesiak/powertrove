using PowerTrove.Application.ViewModels;

namespace PowerTrove.Application.Tests;

public sealed class MultiSelectParameterStateTests
{
    private static ActionParameterChoice[] Fields(params string[] names) => names.Select(n => new ActionParameterChoice(n, n)).ToArray();

    [Fact]
    public void AllFieldsNeedNoTypedInputAndIndividualCheckboxesCreateAnExplicitSelection()
    {
        var state = new MultiSelectParameterState("");
        Assert.Empty(state.Choices);
        Assert.False(state.UpdateChoices(Fields("id", "name", "active")));
        Assert.True(state.UseAll); Assert.Equal("", state.Value);
        state.SetSelected("name", false);
        Assert.False(state.UseAll); Assert.Equal("[\"id\",\"active\"]", state.Value);
        state.Move("active", -1);
        state.Move("active", -1);
        Assert.Equal("[\"active\",\"id\"]", state.Value);
        state.SetUseAll(true);
        Assert.Equal("", state.Value); Assert.Equal(new[] { "id", "name", "active" }, state.Choices.Select(c => c.Value));
    }

    [Fact]
    public void RememberedOrderAndEscapedColumnNamesRoundTripWithoutShowingSerialization()
    {
        var state = new MultiSelectParameterState("[\"quoted\\\"name\",\"id\"]");
        Assert.Empty(state.Choices);
        Assert.False(state.UpdateChoices(Fields("id", "quoted\"name", "other")));
        Assert.Equal(new[] { "quoted\"name", "id", "other" }, state.Choices.Select(c => c.Value));
        Assert.Equal(new[] { "quoted\"name", "id" }, System.Text.Json.JsonSerializer.Deserialize<string[]>(state.Value));
        Assert.DoesNotContain("quoted", state.ToString());
    }

    [Fact]
    public void FlattenChangesDropUnavailableFieldsWithoutSilentlySelectingNewOnes()
    {
        var state = new MultiSelectParameterState("[\"customer\",\"id\"]");
        state.UpdateChoices(Fields("id", "customer"));
        Assert.True(state.UpdateChoices(Fields("id", "customer.name", "customer.city")));
        Assert.True(state.SelectionAdjusted); Assert.Equal("[\"id\"]", state.Value);
        state.Clear(); Assert.Equal("[]", state.Value);
        state.UpdateChoices(Fields("id", "customer")); Assert.Equal("[]", state.Value);
        state.SetSelected("customer", true); Assert.Equal("[\"customer\"]", state.Value);
        Assert.False(state.SelectionAdjusted);
    }

    [Theory]
    [InlineData("broken")][InlineData("[1]")][InlineData("[\"id\",\"id\"]")]
    public void InvalidSavedValuesBecomeAnEmptyEditableSelection(string saved)
    {
        var state = new MultiSelectParameterState(saved); state.UpdateChoices(Fields("id"));
        Assert.Equal("[]", state.Value); Assert.True(state.SelectionAdjusted);
        state.SetSelected("id", true); Assert.Equal("[\"id\"]", state.Value);
    }

    [Fact]
    public void ExcessiveSelectionsAreFlaggedBeforeTransportAndCanBeReduced()
    {
        var fields = Enumerable.Range(0, 30).Select(i => new string('x', 200) + i).ToArray();
        var state = new MultiSelectParameterState(""); state.UpdateChoices(Fields(fields));
        Assert.True(state.IsWithinLimit); state.SetUseAll(false); Assert.False(state.IsWithinLimit);
        state.Clear(); state.SetSelected(fields[0], true); Assert.True(state.IsWithinLimit);
    }
}
