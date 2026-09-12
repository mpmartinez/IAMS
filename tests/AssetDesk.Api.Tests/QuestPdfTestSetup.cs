using System.Runtime.CompilerServices;
using QuestPDF.Infrastructure;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Program.cs sets the QuestPDF license for the running API, but nothing in this test
/// assembly ever ran that line - no earlier test actually rendered a PDF (BuildDepreciationPdf
/// included), so this went unnoticed until a test exercised GeneratePdf() for real and QuestPDF
/// threw for want of a configured license. A module initializer runs once when the test
/// assembly loads, before any test, so every test gets the same Community license Program.cs
/// grants the API.
/// </summary>
internal static class QuestPdfTestSetup
{
    [ModuleInitializer]
    internal static void Configure() => QuestPDF.Settings.License = LicenseType.Community;
}
