using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using Modexa.App.Services;
using Modexa.Core.Engine;
using Modexa.Core.I18n;
using Modexa.Core.Licensing;
using LicenseManager = Modexa.Core.Licensing.LicenseManager;

namespace Modexa.App.Views;

/// <summary>What the license dialog is unlocking.</summary>
public enum LicensePurpose
{
    /// <summary>A purchased mod (.mxa): the key must belong to that product. First one turns the app Plus.</summary>
    Product,
    /// <summary>Modexa Pro: activate a PRO key, then download + verify the Pro engine before switching.</summary>
    Pro
}

/// <summary>
/// License activation dialog.
///
/// Pro flow (the anti-crack path): activate the PRO key on the server -> download the Pro engine
/// from the license-gated endpoint -> load it and confirm it really is the Pro build -> only then
/// report success. A patched exe can't fake this: without the server-issued engine there is no
/// Pro code to run.
/// </summary>
public partial class LicenseWindow : Window
{
    private readonly LicensePurpose _purpose;
    private readonly string? _productId;
    private string? _activatedKey;     // set once the server accepted the key (engine retry skips re-activation)
    private bool _busy;

    public LicenseTier ActivatedTier { get; private set; } = LicenseTier.Free;

    public LicenseWindow(LicensePurpose purpose, string? prompt = null, string? productId = null)
    {
        InitializeComponent();
        _purpose = purpose;
        _productId = productId;
        FlowDirection = LanguageService.Flow;

        if (purpose == LicensePurpose.Pro)
        {
            HeaderText.Text = Loc.Instance["Pro_Activate_Title"];
            SubText.Text = Loc.Instance["Pro_Activate_Sub"];
            HeaderIcon.Text = ""; // star
            KeyHint.Text = LicenseFormat.Example(LicenseTier.Pro);
        }
        else
        {
            KeyHint.Text = LicenseFormat.Example(LicenseTier.Plus, productId);
            HeaderText.Text = Loc.Instance["License_Enter"];
            SubText.Text = string.IsNullOrWhiteSpace(prompt)
                ? Loc.Instance["License_Product_Sub"]
                : $"{prompt}\n{Loc.Instance["License_Product_Sub"]}";
        }
        SubText.Visibility = Visibility.Visible;
        Loaded += (_, _) => txtKey.Focus();
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        // Retry path: key already accepted, only the engine download failed.
        if (_activatedKey != null && _purpose == LicensePurpose.Pro)
        {
            await InstallProEngineAsync(_activatedKey);
            return;
        }

        string key = txtKey.Text.Trim();
        if (!LicenseManager.IsFormatValid(key))
        {
            ShowMessage(Loc.Instance["License_InvalidFormat"], "Danger");
            return;
        }
        if (_purpose == LicensePurpose.Pro && LicenseManager.TierFromKey(key) != LicenseTier.Pro)
        {
            ShowMessage(Loc.Instance["Pro_NotProKey"], "Danger");
            return;
        }
        if (_purpose == LicensePurpose.Product)
        {
            // Mod keys are MDXPLS-…-<product code>; the suffix must match this package's code.
            if (LicenseManager.TierFromKey(key) == LicenseTier.Pro)
            {
                ShowMessage(Loc.Instance["License_ProKeyForMod"], "Danger");
                return;
            }
            if (!LicenseFormat.Covers(key, _productId))
            {
                ShowMessage(Loc.Instance.Format("License_WrongProduct", LicenseFormat.SuffixOf(key), _productId), "Danger");
                return;
            }
        }

        SetBusy(true, indeterminate: true);
        try
        {
            var result = await LicenseManager.ActivateAsync(key, _purpose == LicensePurpose.Product ? _productId : null);
            if (!result.IsValid)
            {
                string msg = result.ErrorCode switch
                {
                    "too_many_activations" => Loc.Instance["License_TooMany"],
                    "NETWORK_ERROR" => Loc.Instance["License_Network"],
                    "APP_UPDATE" => Loc.Instance["License_AppUpdate"],
                    "not_entitled" => Loc.Instance["Gate_NotEntitled"],
                    _ => result.ErrorMessage ?? Loc.Instance["License_InvalidFormat"]
                };
                SetBusy(false);
                ShowMessage(msg, "Danger");
                return;
            }

            LicenseManager.SaveLicense(key, _purpose == LicensePurpose.Product ? _productId : null);
            _activatedKey = key;

            if (_purpose == LicensePurpose.Pro)
            {
                await InstallProEngineAsync(key);
                return;
            }

            ActivatedTier = result.Tier == LicenseTier.Free ? LicenseManager.TierFromKey(key) : result.Tier;
            await SucceedAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            SetBusy(false);
            ShowMessage(Loc.Instance["License_Network"], "Danger");
        }
    }

    /// <summary>Downloads the Pro engine with the activated key and verifies it before switching to Pro.</summary>
    private async Task InstallProEngineAsync(string key)
    {
        SetBusy(true, indeterminate: false);
        ShowMessage(Loc.Instance["Engine_Downloading"], "Text.Secondary");
        var progress = new Progress<int>(p => bar.Value = p);
        try
        {
            var engine = await Task.Run(() => EngineModule.EnsureAsync(key, LicenseTier.Pro, progress));
            if (engine.Flavor != LicenseTier.Pro || engine is not IProModEngine)
                throw new LicenseGateException(LicenseGateException.Refused, "Server returned a non-Pro engine.");

            ActivatedTier = LicenseTier.Pro;
            await SucceedAsync();
        }
        catch (LicenseGateException ex)
        {
            SetBusy(false);
            btnActivate.Content = Loc.Instance["Common_Retry"];
            txtKey.IsEnabled = false;
            ShowMessage($"{Loc.Instance["Pro_EngineFailed"]}\n{GateMessage(ex)}", "Danger");
        }
    }

    private async Task SucceedAsync()
    {
        SetBusy(false);
        btnActivate.IsEnabled = false;
        ShowMessage(Loc.Instance[ActivatedTier == LicenseTier.Pro ? "Pro_Activated" : "License_Success"], "Success");
        await Task.Delay(900);
        DialogResult = true;
    }

    public static string GateMessage(LicenseGateException ex) => ex.Code switch
    {
        LicenseGateException.Network => Loc.Instance["Gate_Network"],
        LicenseGateException.NotEntitled => Loc.Instance["Gate_NotEntitled"],
        LicenseGateException.BadModule => Loc.Instance["Gate_BadModule"],
        _ => Loc.Instance["Gate_Refused"]
    };

    private void ShowMessage(string text, string brushKey)
    {
        lblMsg.Text = text;
        lblMsg.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        lblMsg.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy, bool indeterminate = true)
    {
        _busy = busy;
        bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        bar.IsIndeterminate = busy && indeterminate;
        if (busy && !indeterminate) bar.Value = 0;
        btnActivate.IsEnabled = !busy;
        btnCancel.IsEnabled = !busy;
        if (_activatedKey == null) txtKey.IsEnabled = !busy;
        if (busy && indeterminate) lblMsg.Visibility = Visibility.Collapsed;
    }

    private void Key_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_busy) lblMsg.Visibility = Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // A key that was accepted stays saved even if the user closes before the engine finished.
        DialogResult = false;
    }

    private void Root_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Drag from empty surface only, so the shop link and text box keep their own clicks.
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed
            && e.OriginalSource is Border or Grid or StackPanel)
            DragMove();
    }

    private void Hyperlink_Navigate(object sender, RequestNavigateEventArgs e)
    {
        MainWindow.OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}
