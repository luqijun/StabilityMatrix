﻿using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Helpers;
using StabilityMatrix.Avalonia.Languages;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Models.TagCompletion;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Exceptions;
using StabilityMatrix.Core.Helper.Cache;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Settings;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(PromptCard))]
[ManagedService]
[Transient]
public partial class PromptCardViewModel : LoadableViewModelBase, IParametersLoadableState, IComfyStep
{
    private readonly IModelIndexService modelIndexService;
    private readonly ISettingsManager settingsManager;

    private readonly BaiduTranslationHelper _translator = new BaiduTranslationHelper(
        "20240523002059642",
        "YJS39Mv1alTuhN_xgeYD"
    );

    /// <summary>
    /// Cache of prompt text to tokenized Prompt
    /// </summary>
    private LRUCache<string, Prompt> PromptCache { get; } = new(4);

    public ICompletionProvider CompletionProvider { get; }
    public ITokenizerProvider TokenizerProvider { get; }
    public SharedState SharedState { get; }

    public TextDocument PromptDocumentUI { get; } = new();
    public TextDocument PromptDocument { get; } = new();
    public TextDocument NegativePromptDocumentUI { get; } = new();
    public TextDocument NegativePromptDocument { get; } = new();

    public StackEditableCardViewModel ModulesCardViewModel { get; }

    [ObservableProperty]
    private bool isAutoCompletionEnabled;

    [ObservableProperty]
    private bool isHelpButtonTeachingTipOpen;

    /// <inheritdoc />
    public PromptCardViewModel(
        ICompletionProvider completionProvider,
        ITokenizerProvider tokenizerProvider,
        ISettingsManager settingsManager,
        IModelIndexService modelIndexService,
        ServiceManager<ViewModelBase> vmFactory,
        SharedState sharedState
    )
    {
        this.modelIndexService = modelIndexService;
        this.settingsManager = settingsManager;
        CompletionProvider = completionProvider;
        TokenizerProvider = tokenizerProvider;
        SharedState = sharedState;

        ModulesCardViewModel = vmFactory.Get<StackEditableCardViewModel>(vm =>
        {
            vm.Title = "Styles";
            vm.AvailableModules = [typeof(PromptExpansionModule)];
        });

        settingsManager.RelayPropertyFor(
            this,
            vm => vm.IsAutoCompletionEnabled,
            settings => settings.IsPromptCompletionEnabled,
            true
        );

        this.PromptDocumentUI.TextChanged += (sender, args) =>
        {
            this.PromptDocument.Text = this.PromptDocumentUI.Text;
        };
        this.NegativePromptDocumentUI.TextChanged += (sender, args) =>
        {
            this.NegativePromptDocument.Text = this.NegativePromptDocumentUI.Text;
        };
    }

    partial void OnIsHelpButtonTeachingTipOpenChanging(bool oldValue, bool newValue)
    {
        // If the teaching tip is being closed, save the setting
        if (oldValue && !newValue)
        {
            settingsManager.Transaction(settings =>
            {
                settings.SeenTeachingTips.Add(TeachingTip.InferencePromptHelpButtonTip);
            });
        }
    }

    /// <inheritdoc />
    public override void OnLoaded()
    {
        base.OnLoaded();

        // Show teaching tip for help button if not seen
        if (!settingsManager.Settings.SeenTeachingTips.Contains(TeachingTip.InferencePromptHelpButtonTip))
        {
            IsHelpButtonTeachingTipOpen = true;
        }
    }

    /// <summary>
    /// Applies the prompt step.
    /// Requires:
    /// <list type="number">
    /// <item><see cref="ComfyNodeBuilder.NodeBuilderConnections.Base"/> - Model, Clip</item>
    /// </list>
    /// Provides:
    /// <list type="number">
    /// <item><see cref="ComfyNodeBuilder.NodeBuilderConnections.Base"/> - Conditioning</item>
    /// </list>
    /// </summary>
    public void ApplyStep(ModuleApplyStepEventArgs e)
    {
        // Load prompts
        var positivePrompt = GetPrompt();
        positivePrompt.Process();
        e.Builder.Connections.PositivePrompt = positivePrompt.ProcessedText;

        var negativePrompt = GetNegativePrompt();
        negativePrompt.Process();
        e.Builder.Connections.NegativePrompt = negativePrompt.ProcessedText;

        // Apply modules / styles that may modify the prompt
        ModulesCardViewModel.ApplyStep(e);

        foreach (var modelConnections in e.Builder.Connections.Models.Values)
        {
            if (modelConnections.Model is not { } model || modelConnections.Clip is not { } clip)
                continue;

            // If need to load loras, add a group
            if (positivePrompt.ExtraNetworks.Count > 0)
            {
                var loras = positivePrompt.GetExtraNetworksAsLocalModels(modelIndexService).ToList();

                // Add group to load loras onto model and clip in series
                var lorasGroup = e.Builder.Group_LoraLoadMany(
                    $"Loras_{modelConnections.Name}",
                    model,
                    clip,
                    loras
                );

                // Set last outputs as model and clip
                modelConnections.Model = lorasGroup.Output1;
                modelConnections.Clip = lorasGroup.Output2;
            }

            // Clips
            var positiveClip = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.CLIPTextEncode
                {
                    Name = $"PositiveCLIP_{modelConnections.Name}",
                    Clip = e.Builder.Connections.Base.Clip!,
                    Text = e.Builder.Connections.PositivePrompt
                }
            );
            var negativeClip = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.CLIPTextEncode
                {
                    Name = $"NegativeCLIP_{modelConnections.Name}",
                    Clip = e.Builder.Connections.Base.Clip!,
                    Text = e.Builder.Connections.NegativePrompt
                }
            );

            // Set conditioning from Clips
            modelConnections.Conditioning = (positiveClip.Output, negativeClip.Output);
        }
    }

    /// <summary>
    /// Gets the tokenized Prompt for given text and caches it
    /// </summary>
    private Prompt GetOrCachePrompt(string text)
    {
        // Try get from cache
        if (PromptCache.Get(text, out var cachedPrompt))
        {
            return cachedPrompt!;
        }
        var prompt = Prompt.FromRawText(text, TokenizerProvider);
        PromptCache.Add(text, prompt);
        return prompt;
    }

    /// <summary>
    /// Processes current positive prompt text into a Prompt object
    /// </summary>
    public Prompt GetPrompt() => GetOrCachePrompt(PromptDocument.Text);

    /// <summary>
    /// Processes current negative prompt text into a Prompt object
    /// </summary>
    public Prompt GetNegativePrompt() => GetOrCachePrompt(NegativePromptDocument.Text);

    /// <summary>
    /// Validates both prompts, shows an error dialog if invalid
    /// </summary>
    public async Task<bool> ValidatePrompts()
    {
        var promptText = PromptDocument.Text;
        var negPromptText = NegativePromptDocument.Text;

        try
        {
            var prompt = GetOrCachePrompt(promptText);
            prompt.Process();
            prompt.ValidateExtraNetworks(modelIndexService);
        }
        catch (PromptError e)
        {
            var dialog = DialogHelper.CreatePromptErrorDialog(e, promptText, modelIndexService);
            await dialog.ShowAsync();
            return false;
        }

        try
        {
            var negPrompt = GetOrCachePrompt(negPromptText);
            negPrompt.Process();
        }
        catch (PromptError e)
        {
            var dialog = DialogHelper.CreatePromptErrorDialog(e, negPromptText, modelIndexService);
            await dialog.ShowAsync();
            return false;
        }

        return true;
    }

    [RelayCommand]
    private async Task ShowHelpDialog()
    {
        var md = $"""
                  ## {Resources.Label_Emphasis}
                  ```prompt
                  (keyword)
                  (keyword:1.0)
                  ```
                  
                  ## {Resources.Label_Deemphasis}
                  ```prompt
                  [keyword]
                  ```
                  
                  ## {Resources.Label_EmbeddingsOrTextualInversion}
                  They may be used in either the positive or negative prompts. 
                  Essentially they are text presets, so the position where you place them 
                  could make a difference. 
                  ```prompt
                  <embedding:model>
                  <embedding:model:1.0>
                  ```
                  
                  ## {Resources.Label_NetworksLoraOrLycoris}
                  Unlike embeddings, network tags do not get tokenized to the model, 
                  so the position in the prompt where you place them does not matter.
                  ```prompt
                  <lora:model>
                  <lora:model:1.0>
                  <lyco:model>
                  <lyco:model:1.0>
                  ```
                  
                  ## {Resources.Label_Comments}
                  Inline comments can be marked by a hashtag ‘#’. 
                  All text after a ‘#’ on a line will be disregarded during generation.
                  ```prompt
                  # comments
                  a red cat # also comments
                  detailed
                  ```
                  """;

        var dialog = DialogHelper.CreateMarkdownDialog(md, "Prompt Syntax", TextEditorPreset.Prompt);
        dialog.MinDialogWidth = 800;
        dialog.MaxDialogHeight = 1000;
        dialog.MaxDialogWidth = 1000;
        await dialog.ShowAsync();
    }

    [RelayCommand]
    private async Task DebugShowTokens()
    {
        var prompt = GetPrompt();

        try
        {
            prompt.Process();
        }
        catch (PromptError e)
        {
            await DialogHelper.CreatePromptErrorDialog(e, prompt.RawText, modelIndexService).ShowAsync();
            return;
        }

        var tokens = prompt.TokenizeResult.Tokens;

        var builder = new StringBuilder();

        builder.AppendLine($"## Tokens ({tokens.Length}):");
        builder.AppendLine("```csharp");
        builder.AppendLine(prompt.GetDebugText());
        builder.AppendLine("```");

        try
        {
            if (prompt.ExtraNetworks is { } networks)
            {
                builder.AppendLine($"## Networks ({networks.Count}):");
                builder.AppendLine("```csharp");
                builder.AppendLine(
                    JsonSerializer.Serialize(networks, new JsonSerializerOptions() { WriteIndented = true, })
                );
                builder.AppendLine("```");
            }

            builder.AppendLine("## Formatted for server:");
            builder.AppendLine("```csharp");
            builder.AppendLine(prompt.ProcessedText);
            builder.AppendLine("```");
        }
        catch (PromptError e)
        {
            builder.AppendLine($"##{e.GetType().Name} - {e.Message}");
            builder.AppendLine("```csharp");
            builder.AppendLine(e.StackTrace);
            builder.AppendLine("```");
            throw;
        }

        var dialog = DialogHelper.CreateMarkdownDialog(builder.ToString(), "Prompt Tokens");
        dialog.MinDialogWidth = 800;
        dialog.MaxDialogHeight = 1000;
        dialog.MaxDialogWidth = 1000;
        await dialog.ShowAsync();
    }

    [RelayCommand]
    private void EditorCopy(TextEditor? textEditor)
    {
        textEditor?.Copy();
    }

    [RelayCommand]
    private void EditorPaste(TextEditor? textEditor)
    {
        textEditor?.Paste();
    }

    [RelayCommand]
    private void EditorCut(TextEditor? textEditor)
    {
        textEditor?.Cut();
    }

    /// <inheritdoc />
    public override JsonObject SaveStateToJsonObject()
    {
        return SerializeModel(
            new PromptCardModel
            {
                Prompt = PromptDocument.Text,
                NegativePrompt = NegativePromptDocument.Text,
                ModulesCardState = ModulesCardViewModel.SaveStateToJsonObject()
            }
        );
    }

    /// <inheritdoc />
    public override void LoadStateFromJsonObject(JsonObject state)
    {
        var model = DeserializeModel<PromptCardModel>(state);

        PromptDocument.Text = model.Prompt ?? "";
        NegativePromptDocument.Text = model.NegativePrompt ?? "";

        if (model.ModulesCardState is not null)
        {
            ModulesCardViewModel.LoadStateFromJsonObject(model.ModulesCardState);
        }
    }

    /// <inheritdoc />
    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        PromptDocument.Text = parameters.PositivePrompt ?? "";
        NegativePromptDocument.Text = parameters.NegativePrompt ?? "";
    }

    /// <inheritdoc />
    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        return parameters with
        {
            PositivePrompt = PromptDocument.Text,
            NegativePrompt = NegativePromptDocument.Text
        };
    }

    public async Task TranslateText()
    {
        BaiduTranslationHelper translator = new BaiduTranslationHelper(
            "20240523002059642",
            "YJS39Mv1alTuhN_xgeYD"
        );
        var pos_text = await GetTanslatedText(this.PromptDocumentUI.Text);
        var neg_text = await GetTanslatedText(this.NegativePromptDocumentUI.Text);
        this.PromptDocument.Text = pos_text;
        this.NegativePromptDocument.Text = neg_text;
    }

    public async Task<string> GetTanslatedText(string text)
    {
        BaiduTranslationHelper translator = new BaiduTranslationHelper(
            "20240523002059642",
            "YJS39Mv1alTuhN_xgeYD"
        );
        var res = await _translator.GetTranslation(text);
        return res.trans_result == null ? "" : res.trans_result[0].dst;
    }
}
