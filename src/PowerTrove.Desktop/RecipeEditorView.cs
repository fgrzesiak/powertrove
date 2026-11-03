using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PowerTrove.Application.Configuration;
using PowerTrove.Application.Recipes;
using PowerTrove.Core;

namespace PowerTrove.Desktop;

/// <summary>A recipe being edited. It outlives one editor view while a step's options page is shown.</summary>
public sealed class RecipeDraft
{
    public RecipeDraft(SavedRecipe? recipe)
    {
        Id = recipe?.Id;
        Title = recipe?.Title ?? string.Empty;
        Steps = recipe?.Steps.Select(step => new SavedRecipeStep(step.ActionId, new Dictionary<string, string>(step.Parameters, StringComparer.OrdinalIgnoreCase))).ToList() ?? [];
    }

    /// <summary>Null until the recipe is saved for the first time.</summary>
    public string? Id { get; set; }
    public string Title { get; set; }
    public List<SavedRecipeStep> Steps { get; }
}

/// <summary>
/// Composes a recipe from installed file actions. Each step keeps its own option values, edited on the
/// normal options page; saving registers the recipe as a palette action.
/// </summary>
public sealed class RecipeEditorView : HostedView
{
    private sealed record StepChoice(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    private readonly IActionRegistry _actions;
    private readonly RecipeDraft _draft;
    private readonly IReadOnlyList<StepChoice> _choices;
    private readonly TextBox _name = new() { Header = UiText.RecipeEditorName, PlaceholderText = UiText.RecipeEditorNamePlaceholder, MaxLength = UserRecipes.MaxTitleLength };
    private readonly StackPanel _steps = new() { Spacing = 6 };
    private readonly AutoSuggestBox _add = new() { PlaceholderText = UiText.RecipeEditorChooseAction, MinWidth = 320 };
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private bool _changed;

    public RecipeEditorView(IActionRegistry actions, SavedRecipe? recipe) : this(actions, new RecipeDraft(recipe), changed: false) { }

    public RecipeEditorView(IActionRegistry actions, RecipeDraft draft, bool changed)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _draft = draft ?? throw new ArgumentNullException(nameof(draft));
        _changed = changed;
        Title = draft.Id is null ? UiText.RecipeEditorTitleNew : UiText.RecipeEditorTitleEdit;
        PreferredSize = new Windows.Foundation.Size(720, 680);
        _choices = actions.GetAllActions()
            .Where(UserRecipes.CanBeStep)
            // Multi-select options depend on a concrete selection, which a recipe does not have yet.
            .Where(action => action.Descriptor.Parameters?.All(p => p.Editor == ActionParameterEditor.Default) != false)
            .Select(action => new StepChoice(action.Descriptor.Id, action.Descriptor.Title))
            .OrderBy(choice => choice.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titleBar = new Grid { Padding = new Thickness(20, 0, 140, 0) };
        titleBar.Children.Add(new TextBlock { Text = Title, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(titleBar); TitleBar = titleBar;

        var body = new StackPanel { Spacing = 12, Margin = new Thickness(24, 4, 24, 12) };
        _name.Text = draft.Title;
        _name.TextChanged += (_, _) => { _draft.Title = _name.Text; _changed = true; HideProblem(); };
        body.Children.Add(_name);
        var stepsHeading = new TextBlock { Text = UiText.RecipeEditorSteps, Margin = new Thickness(0, 8, 0, 0) };
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("BodyStrongTextBlockStyle", out var strong) && strong is Style strongStyle)
            stepsHeading.Style = strongStyle;
        body.Children.Add(stepsHeading);
        body.Children.Add(Description(UiText.RecipeEditorStepsHelp));
        body.Children.Add(_steps);

        _add.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var query = sender.Text.Trim();
            sender.ItemsSource = _choices.Where(c => query.Length == 0 || c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)).Take(50).ToArray();
        };
        _add.GotFocus += (sender, _) =>
        {
            if (string.IsNullOrEmpty(_add.Text)) { _add.ItemsSource = _choices.Take(50).ToArray(); _add.IsSuggestionListOpen = true; }
        };
        _add.QuerySubmitted += (sender, args) =>
        {
            var choice = args.ChosenSuggestion as StepChoice
                ?? _choices.FirstOrDefault(c => string.Equals(c.Title, sender.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
            if (choice is null) return;
            AddStep(choice.Id);
            sender.Text = string.Empty;
        };
        AutomationProperties.SetName(_add, UiText.RecipeEditorAddStep);
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        addRow.Children.Add(new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        addRow.Children.Add(_add);
        body.Children.Add(addRow);
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var critical) && critical is Brush criticalBrush)
            _problem.Foreground = criticalBrush;
        AutomationProperties.SetLiveSetting(_problem, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        body.Children.Add(_problem);

        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        var footer = new Grid { Padding = new Thickness(24, 12, 24, 20), ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cancel = CreateCancelButton();
        Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        var save = new Button { Content = UiText.RecipeEditorSave };
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style accentStyle)
            save.Style = accentStyle;
        save.Click += (_, _) => Save();
        Grid.SetColumn(save, 2); footer.Children.Add(save);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        root.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape && !_add.IsSuggestionListOpen) { args.Handled = true; CancelToPalette(); }
        };
        Content = root;
        RenderSteps();
    }

    /// <summary>Unsaved edits survive the shortcut: it brings the editor back instead of a fresh palette.</summary>
    public override bool KeepsWindow => _changed;

    protected internal override Control? InitialFocus => _name;

    private static TextBlock Description(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("SettingsDescriptionStyle", out var style) && style is Style description)
            block.Style = description;
        return block;
    }

    private void AddStep(string actionId)
    {
        if (_draft.Steps.Count >= UserRecipes.MaxSteps) { ShowProblem(new RecipeValidation(RecipeProblem.TooManySteps)); return; }
        _draft.Steps.Add(new SavedRecipeStep(actionId, InitialParameters(actionId)));
        _changed = true;
        HideProblem();
        RenderSteps();
    }

    /// <summary>Each option starts at the value the user last chose for the action, otherwise at its default.</summary>
    private Dictionary<string, string> InitialParameters(string actionId)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var descriptor = _actions.GetAction(actionId)?.Descriptor;
        Dictionary<string, string>? remembered = null;
        if (descriptor?.CanRememberParameters == true)
            (App.Current as App)?.SettingsService?.Settings.ActionPreferences.TryGetValue(actionId, out remembered);
        foreach (var parameter in descriptor?.Parameters ?? [])
        {
            if (remembered is not null && remembered.TryGetValue(parameter.Name, out var value)) values[parameter.Name] = value;
            else if (parameter.DefaultValue is not null) values[parameter.Name] = parameter.DefaultValue;
        }
        return values;
    }

    private void RenderSteps()
    {
        _steps.Children.Clear();
        for (int i = 0; i < _draft.Steps.Count; i++)
        {
            int index = i;
            var action = _actions.GetAction(_draft.Steps[i].ActionId);
            string title = action is null or PowerTrove.Core.Catalog.CatalogAction ? UiText.RecipeStepMissing : action.Descriptor.Title;
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int c = 0; c < 4; c++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = UiText.RecipeEditorStep(i + 1, title), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

            var options = new Button { Content = UiText.RecipeEditorOptions, IsEnabled = action?.Descriptor.Parameters is { Count: > 0 } };
            AutomationProperties.SetName(options, UiText.RecipeEditorStepOptionsNamed(i + 1));
            options.Click += (_, _) => (App.Current as App)?.EditRecipeStep(_draft, index);
            row.Children.Add(Column(options, 1));
            row.Children.Add(Column(IconButton("", UiText.RecipeEditorMoveUp, i > 0, () => Move(index, -1)), 2));
            row.Children.Add(Column(IconButton("", UiText.RecipeEditorMoveDown, i < _draft.Steps.Count - 1, () => Move(index, 1)), 3));
            row.Children.Add(Column(IconButton("", UiText.RecipeEditorRemove, true, () => Remove(index)), 4));
            _steps.Children.Add(row);
        }
    }

    private static FrameworkElement Column(FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        return element;
    }

    private static Button IconButton(string glyph, string name, bool enabled, Action click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, IsEnabled = enabled };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private void Move(int index, int offset)
    {
        int target = index + offset;
        if (target < 0 || target >= _draft.Steps.Count) return;
        (_draft.Steps[index], _draft.Steps[target]) = (_draft.Steps[target], _draft.Steps[index]);
        _changed = true;
        HideProblem();
        RenderSteps();
    }

    private void Remove(int index)
    {
        _draft.Steps.RemoveAt(index);
        _changed = true;
        HideProblem();
        RenderSteps();
    }

    private void Save()
    {
        if (App.Current is not App { SettingsService: IUserRecipeSettings settings } app) return;
        var validation = UserRecipes.Validate(_draft.Title, _draft.Steps, _actions.GetAction);
        if (!validation.IsValid) { ShowProblem(validation); return; }
        var recipe = new SavedRecipe(_draft.Id ?? UserRecipes.NewId(), _draft.Title.Trim(), _draft.Steps.ToArray());
        settings.SaveUserRecipe(recipe);
        _draft.Id = recipe.Id;
        _changed = false;
        app.RefreshUserRecipes();
        app.Shell?.ViewModel.ShowPackNotice(UiText.RecipeEditorSaved(recipe.Title));
        CancelToPalette();
    }

    private void ShowProblem(RecipeValidation validation)
    {
        int step = validation.StepIndex + 1;
        _problem.Text = validation.Problem switch
        {
            RecipeProblem.TitleMissing => UiText.RecipeProblem("TitleMissing"),
            RecipeProblem.TitleTooLong => UiText.RecipeProblem("TitleTooLong", UserRecipes.MaxTitleLength),
            RecipeProblem.TooFewSteps => UiText.RecipeProblem("TooFewSteps", UserRecipes.MinSteps),
            RecipeProblem.TooManySteps => UiText.RecipeProblem("TooManySteps", UserRecipes.MaxSteps),
            RecipeProblem.ActionMissing => UiText.RecipeProblem("ActionMissing", step),
            RecipeProblem.NotAFileAction => UiText.RecipeProblem("NotAFileAction", step),
            RecipeProblem.StepCreatesNoFiles => UiText.RecipeProblem("StepCreatesNoFiles", step),
            RecipeProblem.NestedRecipe => UiText.RecipeProblem("Nested", step),
            _ => string.Empty
        };
        _problem.Visibility = string.IsNullOrEmpty(_problem.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HideProblem() => _problem.Visibility = Visibility.Collapsed;
}
