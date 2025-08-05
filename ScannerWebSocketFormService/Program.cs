using System;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using ScannerWebSocketFormService.Services.Implements;

namespace ScannerWebSocketFormService
{
    static class Program
    {
        /// <summary>
        /// Punto de entrada principal de la aplicación.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Utiliza 'using' para garantizar la liberación del ServiceProvider
            using (var serviceProvider = new ServiceCollection()
                       .AddSingleton<ScannerManager>()
                       .AddSingleton<ImageProcessor>()
                       .AddSingleton<TwainService>()
                       .AddSingleton<WiaService>()
                       .AddSingleton<TempFileManager>()
                       .AddSingleton<WebSocketService>()
                       .AddSingleton<SystemStateManager>()
                       .AddSingleton<Form1>()
                       .BuildServiceProvider())
            {
                // Crea la instancia de Form1 y ejecuta la aplicación
                var form1 = serviceProvider.GetRequiredService<Form1>();
                Application.Run(form1);
            }
        }

        static void ConfigurarInicioSiEsNecesario()
        {
            try
            {
                using RegistryKey clave = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);

                // Solo agregar si no existe
                if (clave?.GetValue("ScannerWebSocketFormService") == null)
                {
                    clave.SetValue("ScannerWebSocketFormService", $"\"{Application.ExecutablePath}\"");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al intentar registrar la app en el inicio de Windows:\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}