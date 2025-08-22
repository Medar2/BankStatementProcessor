using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using BankStatementProcessor.Models;
using Tesseract;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using System.Text.RegularExpressions;
using BankStatementProcessor.Data;
using Microsoft.EntityFrameworkCore;
using ImageMagick; // Add this using statement

namespace BankStatementProcessor.Pages;

public class IndexModel : PageModel
{
    private readonly ILogger<IndexModel> _logger;
    private readonly ApplicationDbContext _context;
    private const string _tessDataPath = "./tessdata"; // Path to tessdata directory

    [BindProperty]
    public new IFormFile File { get; set; }

    [BindProperty]
    public required string DocumentType { get; set; }

    [BindProperty]
    public required string BankName { get; set; }

    [BindProperty]
    public required string AccountNumber { get; set; }

    public required List<Transaction> Transactions { get; set; } = new List<Transaction>();

    public IndexModel(ILogger<IndexModel> logger, ApplicationDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    public void OnGet()
    {
        // No se cargan transacciones en esta página, la página de Transactions se encarga de ello.
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid || File == null || File.Length == 0)
        {
            _logger.LogWarning("Error de validación: ModelState.IsValid={IsValid}, File={FileNull}, File.Length={FileLength}", ModelState.IsValid, File == null, File?.Length ?? 0);
            ModelState.AddModelError(string.Empty, "Por favor, complete todos los campos y seleccione un archivo.");
            return Page();
        }

        _logger.LogInformation("Iniciando procesamiento de archivo. Tipo: {DocumentType}, Banco: {BankName}, Cuenta: {AccountNumber}, Archivo: {FileName}, Tamaño: {FileSize} bytes",
            DocumentType, BankName, AccountNumber, File.FileName, File.Length);

        string extractedText = string.Empty;

        using (var memoryStream = new MemoryStream())
        {
            await File.CopyToAsync(memoryStream);
            memoryStream.Position = 0; // Reset stream position

            if (DocumentType == "pdf")
            {
                _logger.LogInformation("Extrayendo texto de PDF.");
                extractedText = ExtractTextFromPdf(memoryStream);
                _logger.LogInformation("Texto Extraído (PDF):\n{ExtractedText}", extractedText);
            }
            else if (DocumentType == "image")
            {
                _logger.LogInformation("Extrayendo texto de Imagen.");
                var tempFilePath = Path.GetTempFileName();
                await System.IO.File.WriteAllBytesAsync(tempFilePath, memoryStream.ToArray());
                extractedText = ExtractTextFromImage(tempFilePath);
                System.IO.File.Delete(tempFilePath); // Clean up temp file

                _logger.LogInformation("Texto Extraído (Imagen):\n{ExtractedText}", extractedText);
            }
        }

        var newTransactions = ParseTransactions(extractedText);
        _logger.LogInformation("Se encontraron {Count} transacciones en el texto extraído.", newTransactions.Count);

        if (newTransactions.Any())
        {
            _logger.LogInformation("Iniciando guardado de transacciones en la base de datos.");
            foreach (var transaction in newTransactions)
            {
                _context.Transactions.Add(transaction);
                _logger.LogInformation("Añadida transacción para guardar: Fecha={Fecha}, Comentarios={Comentarios}, Monto={Monto}", transaction.Fecha.ToShortDateString(), transaction.Comentarios, transaction.Monto);
            }
            try
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation("Transacciones guardadas exitosamente en la base de datos.");
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al guardar transacciones en la base de datos. Mensaje: {Message}", ex.Message);
                ModelState.AddModelError(string.Empty, "Ocurrió un error al guardar las transacciones. Por favor, inténtelo de nuevo.");
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Un error inesperado ocurrió al guardar transacciones. Mensaje: {Message}", ex.Message);
                ModelState.AddModelError(string.Empty, "Ocurrió un error inesperado al guardar las transacciones. Por favor, inténtelo de nuevo.");
                return Page();
            }
        }
        else
        {
            _logger.LogWarning("No se encontraron transacciones para guardar en la base de datos.");
            ModelState.AddModelError(string.Empty, "No se pudieron extraer transacciones del documento. Por favor, verifique el formato.");
            return Page();
        }

        return RedirectToPage("./Transactions");
    }

    private List<Transaction> ParseTransactions(string text)
{
    _logger.LogInformation("Iniciando ParseTransactions. Texto a parsear:\n{Text}", text);

    var transactions = new List<Transaction>();

    // Regex para: Fecha, Comentarios, Monto, Balance, Cheque (opcional)
    var regex = new Regex(
        @"^(\d{2}/\d{2}/\d{4})\s+(.+?)\s+RD\$[\s]*([\d.,\-]+)\s+RD\$[\s]*([\d.,]+)(?:\s+(\S+))?",
        RegexOptions.Multiline);

    foreach (Match match in regex.Matches(text))
    {
        _logger.LogInformation("Coincidencia de regex encontrada: {MatchValue}", match.Value);
        if (match.Groups.Count >= 5)
        {
            try
            {
                DateTime fecha;
                if (!DateTime.TryParse(match.Groups[1].Value, out fecha))
                {
                    _logger.LogWarning("No se pudo parsear la fecha: {DateString}", match.Groups[1].Value);
                    continue;
                }

                string comentarios = match.Groups[2].Value.Trim();

                string montoString = match.Groups[3].Value.Replace(".", "").Replace(",", ".").Replace("-", "").Trim();
                decimal monto;
                if (!decimal.TryParse(montoString, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out monto))
                {
                    _logger.LogWarning("No se pudo parsear el monto: {MontoString}", montoString);
                    continue;
                }
                if (match.Groups[3].Value.Contains("-"))
                {
                    monto = -monto;
                }

                string balanceString = match.Groups[4].Value.Replace(".", "").Replace(",", ".").Trim();
                decimal balance;
                if (!decimal.TryParse(balanceString, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out balance))
                {
                    _logger.LogWarning("No se pudo parsear el balance: {BalanceString}", balanceString);
                    continue;
                }
                string cheque = match.Groups.Count > 5 ? match.Groups[5].Value.Trim() : "";

                transactions.Add(new Transaction
                {
                    Fecha = fecha,
                    Comentarios = comentarios,
                    Monto = monto,
                    Balance = balance,
                    Cheque = cheque
                });
                _logger.LogInformation("Transacción parseada con éxito: Fecha={Fecha}, Comentarios={Comentarios}, Monto={Monto}", fecha.ToShortDateString(), comentarios, monto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error durante el parseo de una transacción: {Line}", match.Value);
            }
        }
        else
        {
            _logger.LogWarning("La coincidencia de regex no tiene el número esperado de grupos: {MatchValue}", match.Value);
        }
    }

    _logger.LogInformation("ParseTransactions finalizado. Total de transacciones encontradas: {Count}", transactions.Count);
    return transactions;
}

    private string ExtractTextFromImage(string imagePath)
    {
        try
        {
            using (var image = new MagickImage(imagePath))
            {
                // Preprocesamiento de la imagen para mejorar el OCR
                image.ColorType = ColorType.Grayscale; // Convertir a escala de grises
                image.Deskew(new Percentage(80)); // Corregir inclinación
                image.Normalize(); // Mejorar contraste

                using (var processedMemoryStream = new MemoryStream())
                {
                    image.Format = MagickFormat.Png;
                    image.Write(processedMemoryStream);
                    processedMemoryStream.Position = 0;

                    using (var engine = new TesseractEngine(_tessDataPath, "eng+spa", EngineMode.Default))
                    {
                        using (var img = Pix.LoadFromMemory(processedMemoryStream.ToArray()))
                        {
                            using (var page = engine.Process(img))
                            {
                                return page.GetText();
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting text from image.");
            return "Error extracting text from image: " + ex.Message;
        }
    }

    private string ExtractTextFromPdf(Stream pdfStream)
{
    var allText = new System.Text.StringBuilder();

    try
    {
        using (var document = PdfDocument.Open(pdfStream))
        {
            foreach (var page in document.GetPages())
            {
                allText.Append(page.Text);
            }
        }

        string text = allText.ToString();
        string textLower = text.ToLowerInvariant();

        // Nueva lógica: Si no hay al menos 2 líneas que parecen transacciones (fecha al inicio), activa OCR
        int transaccionesDetectadas = Regex.Matches(text, @"^\d{2}/\d{2}/\d{4}", RegexOptions.Multiline).Count;

        if (allText.Length < 100 || transaccionesDetectadas < 2)
        {
            _logger.LogInformation("PdfPig no detectó suficientes transacciones. Recurriendo a OCR para PDF.");
            pdfStream.Position = 0; // Resetear el stream para Magick.NET
            using (var images = new MagickImageCollection())
            {
                var settings = new MagickReadSettings();
                settings.Density = new Density(300); // 300 DPI para mejor calidad de OCR

                images.Read(pdfStream, settings);

                foreach (var image in images)
                {
                    using (var ms = new MemoryStream())
                    {
                        image.Format = MagickFormat.Png;
                        image.Write(ms);
                        ms.Position = 0;

                        using (var engine = new TesseractEngine(_tessDataPath, "eng+spa", EngineMode.Default))
                        {
                            using (var img = Pix.LoadFromMemory(ms.ToArray()))
                            {
                                using (var page = engine.Process(img))
                                {
                                    allText.Append(page.GetText());
                                    _logger.LogInformation("Texto OCR de página PDF: {OcrText}", page.GetText().Replace("\n", " ").Substring(0, Math.Min(page.GetText().Length, 100)) + "...");
                                }
                            }
                        }
                    }
                }
            }
        }

        return allText.ToString();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error al extraer texto de PDF (incluyendo posible OCR). Mensaje: {Message}", ex.Message);
        return "Error al extraer texto de PDF: " + ex.Message;
    }
}
}
