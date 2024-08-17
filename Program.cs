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
        private static string cacheFilePath;
        private static List<ChartData> jsonData = new List<ChartData>();
        private static Dictionary<string, CachedCompanyData> cache = new Dictionary<string, CachedCompanyData>();
        private static int refreshCount = 0;

        static async Task Main(string[] args)
        {
            LoadConfiguration();

            folderPath = configuration["AppSettings:FolderPath"];
            containerName = configuration["AppSettings:ContainerName"];

            dataJsonPath = Path.Combine(folderPath, "data.json");
            cacheFilePath = Path.Combine(folderPath, "cache.json");

            LoadCache();

            //while (true)
            //{
            await ProcessChartsAsync();
            SaveCache();
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

        private static void LoadCache()
        {
            if (File.Exists(cacheFilePath))
            {
                var cacheContent = File.ReadAllText(cacheFilePath);
                cache = JsonSerializer.Deserialize<Dictionary<string, CachedCompanyData>>(cacheContent);
            }
        }

        private static void SaveCache()
        {
            var cacheContent = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(cacheFilePath, cacheContent);
        }

        private static async Task ProcessChartsAsync()
        {
            var chartFiles = Directory.GetFiles(folderPath, "*.PNG");

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
            // Check cache first
            if (cache.ContainsKey(ticker) && cache[ticker].LastUpdated >= DateTime.Today)
            {
                return cache[ticker].CompanyName;
            }

            // Refresh cache if the item is old and under the refresh limit
            if (cache.ContainsKey(ticker) && refreshCount < 20)
            {
                refreshCount++;
                return await RefreshCompanyNameAsync(ticker);
            }

            // If not in cache, fetch from API and cache the result
            if (!cache.ContainsKey(ticker))
            {
                return await RefreshCompanyNameAsync(ticker);
            }

            return cache[ticker].CompanyName;
        }

        private static async Task<string> RefreshCompanyNameAsync(string ticker)
        {
            using var httpClient = new HttpClient();
            var url = $"https://www.alphavantage.co/query?function=SYMBOL_SEARCH&keywords={ticker}&apikey={apiKey}";
            var response = await httpClient.GetStringAsync(url);
            var searchResults = JsonSerializer.Deserialize<AlphaVantageResponse>(response);
            var companyName = searchResults?.bestMatches?.FirstOrDefault()?.Name ?? "Unknown";

            cache[ticker] = new CachedCompanyData
            {
                CompanyName = companyName,
                LastUpdated = DateTime.Now
            };

            return companyName;
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
            var lastWriteTime = File.GetLastWriteTime(filePath);  // Get the last write time of the file

            var chartData = new ChartData
            {
                TickerSymbol = ticker,
                CompanyName = companyName,
                UpdatedDate = lastWriteTime.ToString("yyyy-MM-dd"),  // Use the last write time
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

    public class CachedCompanyData
    {
        public string CompanyName { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
