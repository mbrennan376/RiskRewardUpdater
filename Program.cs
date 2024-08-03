using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using System.Text.Json.Serialization;

namespace ChartUploader
{
    class Program
    {
        private static IConfiguration configuration;
        private static string folderPath;
        private static string storageAccountConnectionString;
        private static string containerName;
        private static string apiKey;
        private static string dataJsonPath;
        private static List<ChartData> jsonData = new List<ChartData>();

        static async Task Main(string[] args)
        {
            LoadConfiguration();

            folderPath = configuration["AppSettings:FolderPath"];
            containerName = configuration["AppSettings:ContainerName"];
            
            dataJsonPath = Path.Combine(folderPath, "data.json");

            //while (true)
            //{
                await ProcessChartsAsync();
               // await Task.Delay(TimeSpan.FromMinutes(5));
           // }
        }

        private static void LoadConfiguration()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddUserSecrets<Program>();

            configuration = builder.Build();

            storageAccountConnectionString = configuration["AzureStorage:ConnectionString"];
            apiKey = configuration["AlphaVantage:ApiKey"];
        }

        private static async Task ProcessChartsAsync()
        {
            var chartFiles = Directory.GetFiles(folderPath, "*.jpg");

            foreach (var chartFile in chartFiles)
            {
                var ticker = Path.GetFileNameWithoutExtension(chartFile);
                var companyName = await GetCompanyNameAsync(ticker);

                await UploadToAzureBlobAsync(chartFile, $"charts/{Path.GetFileName(chartFile)}");

                UpdateDataJson(chartFile, ticker, companyName);
            }

            await File.WriteAllTextAsync(dataJsonPath, JsonSerializer.Serialize(jsonData, new JsonSerializerOptions { WriteIndented = true }));

            await UploadToAzureBlobAsync(dataJsonPath, "data.json");
        }

        private static async Task<string> GetCompanyNameAsync(string ticker)
        {
            using var httpClient = new HttpClient();
            var url = $"https://www.alphavantage.co/query?function=SYMBOL_SEARCH&keywords={ticker}&apikey={apiKey}";
            var response = await httpClient.GetStringAsync(url);
            var searchResults = JsonSerializer.Deserialize<AlphaVantageResponse>(response);
            return searchResults?.bestMatches?.FirstOrDefault()?.Name ?? "Unknown";
        }

        private static async Task UploadToAzureBlobAsync(string filePath, string blobName)
        {
            var blobServiceClient = new BlobServiceClient(storageAccountConnectionString);
            var blobContainerClient = blobServiceClient.GetBlobContainerClient(containerName);
            var blobClient = blobContainerClient.GetBlobClient(blobName);

            await blobClient.UploadAsync(filePath, true);
        }

        private static void UpdateDataJson(string filePath, string ticker, string companyName)
        {
            var chartFileName = Path.GetFileName(filePath);
            var chartData = new ChartData
            {
                TickerSymbol = ticker,
                CompanyName = companyName,
                UpdatedDate = DateTime.Now.ToString("yyyy-MM-dd"),
                ChartFilename = $"charts/{chartFileName}",
                Comments = ""
            };

            jsonData.Add(chartData);
        }
    }

    public class ChartData
    {
        public string TickerSymbol { get; set; }
        public string CompanyName { get; set; }
        public string UpdatedDate { get; set; }
        public string ChartFilename { get; set; }
        public string Comments { get; set; }
    }

    public class AlphaVantageResponse
    {
        public List<BestMatch> bestMatches { get; set; }
    }

    public class BestMatch
    {
        [JsonPropertyName("2. name")]
        public string Name { get; set; }
    }
}
