namespace ScannerWebSocketFormService;

partial class Form1
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;
    private System.Windows.Forms.NotifyIcon NotifyScannerService;
    private System.Windows.Forms.ContextMenuStrip contextMenuStrip;
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (NotifyScannerService != null)
            {
                NotifyScannerService.Visible = false;
                NotifyScannerService.Dispose();
                NotifyScannerService = null;
            }
            components?.Dispose();
        }
        base.Dispose(disposing);
    }



    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        var resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
        
        // Crear NotifyIcon
        NotifyScannerService = new System.Windows.Forms.NotifyIcon(components)
        {
            Icon = (System.Drawing.Icon)resources.GetObject("NotifyScannerService.Icon"), Text = "Scanner Service", Visible = true
        };
        
        // Crear menú contextual
        contextMenuStrip = new System.Windows.Forms.ContextMenuStrip(components);
        contextMenuStrip.Items.Add("Estado", null, (sender, args) =>
        {
            NotifyScannerService.BalloonTipTitle = "Servicio de escáner";
            NotifyScannerService.BalloonTipText = "El servicio está en línea y funcionando correctamente.";
            NotifyScannerService.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
            NotifyScannerService.ShowBalloonTip(4000); // Mostrar por 4 segundos
        });
        contextMenuStrip.Items.Add("Salir", null, (sender, e) => Application.Exit());
        NotifyScannerService.ContextMenuStrip = contextMenuStrip;
        
        // Mostrar notificación de inicio
        NotifyScannerService.BalloonTipTitle = "Servicio de escáner";
        NotifyScannerService.BalloonTipText = "El servicio se ha inicializado correctamente.";
        NotifyScannerService.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
        NotifyScannerService.ShowBalloonTip(4000); // Mostrar por 4 segundos
        
        // Configuración del formulario
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(800, 450);
        Text = "Form1";
        ResumeLayout(false);
    }
}