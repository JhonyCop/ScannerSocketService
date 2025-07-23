using ScannerWebSocketFormService.Models;

namespace ScannerWebSocketFormService;

public partial class UnifiedDeviceSelectorForm : Form
{
    public ScannerDevice? SelectedDevice { get; private set; }
    
    private readonly List<ScannerDevice> _initialDevices;
    private List<ScannerDevice> _currentDevices;
    private List<ScannerDevice> _sortedDevices;
    private readonly Func<Task<List<ScannerDevice>>>? _refreshDevicesCallback;
    
    private ListBox _listBoxDevices;
    private Button _buttonSelect;
    private Button _buttonCancel;
    private Button _buttonRefresh;
    private Label _labelTitle;
    private Label _labelCount;
    private Label _labelStatus;
    private ProgressBar _progressBar;
    private Panel _buttonPanel;

    public UnifiedDeviceSelectorForm(List<ScannerDevice> devices, Func<Task<List<ScannerDevice>>>? refreshCallback)
    {
        _initialDevices = devices ?? new List<ScannerDevice>();
        _currentDevices = new List<ScannerDevice>(_initialDevices);
        _sortedDevices = new List<ScannerDevice>();
        _refreshDevicesCallback = refreshCallback;
        
        InitializeComponent();
        ConfigureForm();
        SetupEventHandlers();
        LoadDevicesOptimized();
    }

    private void ConfigureForm()
    {
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.TopMost = true;
        this.ShowInTaskbar = true;
        this.KeyPreview = true;
        
        this.AcceptButton = _buttonSelect;
        this.CancelButton = _buttonCancel;
    }

    private void SetupEventHandlers()
    {
        _listBoxDevices.KeyDown += ListBoxDevices_KeyDown;
        this.KeyDown += Form_KeyDown;
    }

    private void ListBoxDevices_DrawItem(object sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        
        if (e.Index >= 0 && e.Index < _sortedDevices.Count)
        {
            DrawDeviceItem(e);
        }
        else if (e.Index >= 0)
        {
            DrawStatusMessage(e);
        }
        
        e.DrawFocusRectangle();
    }

    private void DrawDeviceItem(DrawItemEventArgs e)
    {
        var device = _sortedDevices[e.Index];
        var isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        
        var backColor = isSelected ? Color.FromArgb(0, 120, 215) : Color.White;
        var textColor = isSelected ? Color.White : Color.FromArgb(64, 64, 64);
        var iconColor = isSelected ? Color.White : GetDeviceTypeColor(device.Type);
        
        // Fondo
        using var brush = new SolidBrush(backColor);
        e.Graphics.FillRectangle(brush, e.Bounds);
        
        // Icono
        DrawDeviceIcon(e.Graphics, device.Type, iconColor, e.Bounds);
        
        // Texto
        DrawDeviceText(e.Graphics, device, textColor, e.Bounds);
    }

    private void DrawDeviceIcon(Graphics graphics, ScannerType type, Color color, Rectangle bounds)
    {
        var icon = GetTypeIcon(type);
        var iconBounds = new Rectangle(bounds.X + 12, bounds.Y + 3, 20, 18);
        
        using var iconBrush = new SolidBrush(color);
        using var iconFont = new Font("Segoe UI", 10F);
        graphics.DrawString(icon.Split(' ')[0], iconFont, iconBrush, iconBounds);
    }

    private void DrawDeviceText(Graphics graphics, ScannerDevice device, Color color, Rectangle bounds)
    {
        var textBounds = new Rectangle(bounds.X + 38, bounds.Y + 3, bounds.Width - 45, 18);
        var displayText = $"{device.Type} - {device.Name}";
        
        using var textBrush = new SolidBrush(color);
        using var textFont = new Font("Segoe UI", 9F, FontStyle.Regular);
        graphics.DrawString(displayText, textFont, textBrush, textBounds);
    }

    private void DrawStatusMessage(DrawItemEventArgs e)
    {
        var message = _listBoxDevices.Items[e.Index].ToString();
        var textBounds = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 3, e.Bounds.Width - 20, 18);
        
        using var brush = new SolidBrush(Color.FromArgb(120, 120, 120));
        using var font = new Font("Segoe UI", 9F, FontStyle.Italic);
        e.Graphics.DrawString(message, font, brush, textBounds);
    }
    
    private Color GetDeviceTypeColor(ScannerType type)
    {
        return type switch
        {
            ScannerType.TWAIN => Color.FromArgb(138, 43, 226), 
            ScannerType.WIA => Color.FromArgb(30, 144, 255),   
            _ => Color.FromArgb(100, 100, 100)
        };
    }

    private string GetTypeIcon(ScannerType type)
    {
        return type switch
        {
            ScannerType.TWAIN => "🖨️",
            ScannerType.WIA => "🖨️",
            _ => ""
        };
    }
    
    private void ListBoxDevices_SelectedIndexChanged(object sender, EventArgs e)
    {
        var hasValidSelection = _listBoxDevices.SelectedIndex >= 0 && 
                               _listBoxDevices.SelectedIndex < _sortedDevices.Count;
        
        UpdateSelectionUI(hasValidSelection);
    }

    private void UpdateSelectionUI(bool hasValidSelection)
    {
        if (hasValidSelection)
        {
            var selectedDevice = _sortedDevices[_listBoxDevices.SelectedIndex];
            SetStatusMessage($"📋 Seleccionado: {selectedDevice.Type} - {selectedDevice.Name}", 
                           Color.FromArgb(0, 120, 215));
            SetButtonState(true, Color.FromArgb(40, 167, 69));
        }
        else
        {
            SetStatusMessage("Selecciona un dispositivo de la lista", Color.FromArgb(100, 100, 100));
            SetButtonState(false, Color.FromArgb(180, 180, 180));
        }
    }

    private void SetStatusMessage(string message, Color color)
    {
        _labelStatus.Text = message;
        _labelStatus.ForeColor = color;
    }

    private void SetButtonState(bool enabled, Color backColor)
    {
        _buttonSelect.Enabled = enabled;
        _buttonSelect.BackColor = backColor;
    }

    private void LoadDevicesOptimized()
    {
        _listBoxDevices.BeginUpdate();
        _listBoxDevices.Items.Clear();
        
        if (_currentDevices.Count == 0)
        {
            HandleNoDevices();
            return;
        }

        PopulateDeviceList();
        ConfigureUIForDevices();
        _listBoxDevices.EndUpdate();
    }

    private void HandleNoDevices()
    {
        _listBoxDevices.Items.Add(" No se encontraron dispositivos de escaneo");
        _sortedDevices.Clear();
        
        SetButtonState(false, Color.FromArgb(180, 180, 180));
        _progressBar.Visible = false;
        
        _labelCount.Text = " No se detectaron dispositivos de escáner en el sistema";
        _labelCount.ForeColor = Color.FromArgb(220, 20, 60);
        
        SetStatusMessage(" Conecta un escáner USB o de red al ordenador y presiona 'Actualizar'", 
                        Color.FromArgb(255, 140, 0));
        
        ShowNoDevicesMessage();
        _listBoxDevices.EndUpdate();
    }

    private void ShowNoDevicesMessage()
    {
        MessageBox.Show(this,
            "No hay dispositivos de escáner conectados al ordenador.\n\n" +
            "Por favor:\n" +
            "• Conecta el escáner via USB\n" +
            "• Verifica que esté encendido\n" +
            "• Presiona 'Actualizar' después de conectar",
            "No hay dispositivos conectados",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void PopulateDeviceList()
    {
        _sortedDevices = _currentDevices
            .OrderBy(d => d.Type == ScannerType.TWAIN ? 0 : 1)
            .ThenBy(d => d.Name)
            .ToList();

        foreach (var device in _sortedDevices)
        {
            _listBoxDevices.Items.Add(device);
        }
    }

    private void ConfigureUIForDevices()
    {
        if (_listBoxDevices.Items.Count > 0)
        {
            _listBoxDevices.SelectedIndex = 0;
            _progressBar.Visible = false;
            
            var deviceCount = _sortedDevices.Count;
            _labelCount.Text = deviceCount == 1 ? " 1 dispositivo disponible" : $" {deviceCount} dispositivos disponibles";
            _labelCount.ForeColor = Color.FromArgb(40, 167, 69);
            
            var statusMessage = deviceCount == 1 ? " Dispositivo listo para escanear" : " Selecciona el dispositivo deseado";
            var statusColor = deviceCount == 1 ? Color.FromArgb(40, 167, 69) : Color.FromArgb(0, 120, 215);
            SetStatusMessage(statusMessage, statusColor);
        }
        else
        {
            SetButtonState(false, Color.FromArgb(180, 180, 180));
            _progressBar.Visible = false;
            SetStatusMessage(" No hay dispositivos disponibles", Color.FromArgb(220, 20, 60));
        }
    }

    private async Task RefreshDevicesAsync()
    {
        if (_refreshDevicesCallback == null)
        {
            SetStatusMessage(" Función de actualización no disponible", Color.FromArgb(220, 20, 60));
            return;
        }

        try
        {
            SetRefreshingState(true);
            var updatedDevices = await ExecuteRefreshWithTimeout();
            ProcessRefreshResult(updatedDevices);
        }
        catch (Exception ex)
        {
            HandleRefreshError(ex);
        }
        finally
        {
            SetRefreshingState(false);
        }
    }

    private void SetRefreshingState(bool isRefreshing)
    {
        _buttonRefresh.Enabled = !isRefreshing;
        _buttonSelect.Enabled = !isRefreshing;
        _progressBar.Visible = isRefreshing;
        _progressBar.Style = isRefreshing ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        
        if (isRefreshing)
        {
            SetStatusMessage(" Buscando dispositivos recién conectados...", Color.FromArgb(0, 120, 215));
            _listBoxDevices.BeginUpdate();
            _listBoxDevices.Items.Clear();
            _listBoxDevices.Items.Add("🔍 Detectando dispositivos nuevos...");
            _listBoxDevices.EndUpdate();
        }
    }

    private async Task<List<ScannerDevice>> ExecuteRefreshWithTimeout()
    {
        var refreshTask = _refreshDevicesCallback();
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(8));
        
        var completedTask = await Task.WhenAny(refreshTask, timeoutTask);
        
        if (completedTask == refreshTask)
        {
            return await refreshTask ?? new List<ScannerDevice>();
        }
        else
        {
            SetStatusMessage(" Búsqueda tomó demasiado tiempo - usando dispositivos previos", 
                           Color.FromArgb(255, 140, 0));
            return _currentDevices;
        }
    }

    private void ProcessRefreshResult(List<ScannerDevice> updatedDevices)
    {
        _currentDevices = updatedDevices;
        LoadDevicesOptimized();
        
        if (_currentDevices.Count > 0)
        {
            var newCount = _currentDevices.Count - _initialDevices.Count;
            var message = newCount > 0 
                ? $"🎉 ¡{newCount} dispositivo(s) nuevo(s) encontrado(s)!" 
                : " Lista actualizada correctamente";
            SetStatusMessage(message, Color.FromArgb(40, 167, 69));
        }
        else
        {
            SetStatusMessage(" No se encontraron dispositivos", Color.FromArgb(255, 140, 0));
        }
    }

    private void HandleRefreshError(Exception ex)
    {
        SetStatusMessage($" Error actualizando: {ex.Message}", Color.FromArgb(220, 20, 60));
        _currentDevices = new List<ScannerDevice>(_initialDevices);
        LoadDevicesOptimized();
    }

    private void SelectDevice()
    {
        if (_listBoxDevices.SelectedIndex >= 0 && _listBoxDevices.SelectedIndex < _sortedDevices.Count)
        {
            SelectedDevice = _sortedDevices[_listBoxDevices.SelectedIndex];
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }

    private void CancelSelection()
    {
        SelectedDevice = null;
        this.DialogResult = DialogResult.Cancel;
        this.Close();
    }

    private void ListBoxDevices_KeyDown(object sender, KeyEventArgs e)
    {
        HandleKeyPress(e);
    }

    private void Form_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !_buttonSelect.Enabled)
            return;
            
        HandleKeyPress(e);
    }

    private void HandleKeyPress(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter:
                e.Handled = true;
                SelectDevice();
                break;
            case Keys.Escape:
                e.Handled = true;
                CancelSelection();
                break;
            case Keys.F5:
                if (_buttonRefresh.Enabled)
                {
                    e.Handled = true;
                    _ = RefreshDevicesAsync();
                }
                break;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (this.DialogResult == DialogResult.None)
        {
            this.DialogResult = DialogResult.Cancel;
            SelectedDevice = null;
        }
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        EnsureFormVisibility();
        _listBoxDevices.Focus();
    }

    private void EnsureFormVisibility()
    {
        this.CenterToScreen();
        this.TopMost = true;
        this.BringToFront();
        this.Activate();
    }
}