using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class MainForm : Form
{
    private readonly AppConfig _config = AppConfig.Load();
    private readonly DeviceService _service;
    private readonly ListBox _devicesList = new();
    private readonly Button _refreshButton = new();
    private readonly CheckBox _powerToggle = new();
    private readonly Label _statusLabel = new();
    private readonly List<DeviceInfo> _devices = new();
    private bool _suppressToggleEvent;

    public MainForm()
    {
        _service = new DeviceService(_config);
        Text = "Controle Remoto LAN";
        MinimumSize = new Size(520, 360);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(12)
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _devicesList.Dock = DockStyle.Fill;
        _devicesList.SelectedIndexChanged += async (_, _) => await UpdateSelectionAsync();

        var buttonsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false
        };

        _refreshButton.Text = "Atualizar";
        _refreshButton.Width = 120;
        _refreshButton.Click += async (_, _) => await RefreshDevicesAsync();

        _powerToggle.Text = "OFF";
        _powerToggle.Appearance = Appearance.Button;
        _powerToggle.TextAlign = ContentAlignment.MiddleCenter;
        _powerToggle.Width = 120;
        _powerToggle.Height = 32;
        _powerToggle.Enabled = false;
        _powerToggle.CheckedChanged += async (_, _) => await OnToggleChangedAsync();

        buttonsPanel.Controls.Add(_refreshButton);
        buttonsPanel.Controls.Add(_powerToggle);

        _statusLabel.Text = "Pronto.";
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.AutoSize = true;

        layout.Controls.Add(_devicesList, 0, 0);
        layout.SetColumnSpan(_devicesList, 1);
        layout.Controls.Add(buttonsPanel, 1, 0);
        layout.Controls.Add(_statusLabel, 0, 1);
        layout.SetColumnSpan(_statusLabel, 2);

        Controls.Add(layout);

        Shown += async (_, _) => await RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        SetBusy(true, "Procurando dispositivos...");

        _devices.Clear();
        _devicesList.Items.Clear();

        List<DeviceInfo> devices;
        try
        {
            devices = await _service.DiscoverDevicesAsync();
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Erro: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        foreach (DeviceInfo device in devices)
        {
            _devices.Add(device);
            _devicesList.Items.Add(device.ToDisplayString());
        }

        if (_devices.Count == 0)
        {
            SetBusy(false, "Nenhum dispositivo encontrado.");
        }
        else
        {
            SetBusy(false, $"Encontrados: {_devices.Count} dispositivo(s). Selecione um.");
        }

        await UpdateSelectionAsync();
    }

    private async Task OnToggleChangedAsync()
    {
        if (_suppressToggleEvent)
        {
            return;
        }

        int index = _devicesList.SelectedIndex;
        if (index < 0 || index >= _devices.Count)
        {
            return;
        }

        DeviceInfo device = _devices[index];
        if (!device.SupportsPowerControl)
        {
            SetBusy(false, "Dispositivo nao suporta ON/OFF.");
            MessageBox.Show(this, "Dispositivo nao suporta controle ON/OFF.", "Aviso", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        bool isOn = _powerToggle.Checked;
        SetBusy(true, isOn ? "Enviando ON..." : "Enviando OFF...");

        try
        {
            await _service.SetPowerAsync(device, isOn);
            SetBusy(false, isOn ? "Comando ON enviado." : "Comando OFF enviado.");
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Erro: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            await UpdateSelectionAsync();
        }
    }

    private async Task UpdateSelectionAsync()
    {
        int index = _devicesList.SelectedIndex;
        bool canControl = index >= 0 && index < _devices.Count && _devices[index].SupportsPowerControl;
        _powerToggle.Enabled = canControl;

        if (!canControl)
        {
            SetToggleState(null);
            return;
        }

        DeviceInfo device = _devices[index];
        SetBusy(true, "Lendo status...");

        try
        {
            bool? state = await _service.GetPowerStateAsync(device);
            SetToggleState(state);
            SetBusy(false, state.HasValue ? (state.Value ? "Ligado" : "Desligado") : "Status indisponivel");
        }
        catch (Exception ex)
        {
            SetToggleState(null);
            SetBusy(false, $"Erro: {ex.Message}");
        }
    }

    private void SetToggleState(bool? isOn)
    {
        _suppressToggleEvent = true;
        if (isOn.HasValue)
        {
            _powerToggle.Checked = isOn.Value;
            _powerToggle.Text = isOn.Value ? "ON" : "OFF";
        }
        else
        {
            _powerToggle.Checked = false;
            _powerToggle.Text = "?";
        }

        _suppressToggleEvent = false;
    }

    private void SetBusy(bool isBusy, string status)
    {
        _refreshButton.Enabled = !isBusy;
        _devicesList.Enabled = !isBusy;
        _statusLabel.Text = status;
        UseWaitCursor = isBusy;
        Cursor = isBusy ? Cursors.WaitCursor : Cursors.Default;
    }
}
