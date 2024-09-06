using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using RiskRewardUpdater.Entities;
using System.Text.Json;

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

        // Define the refresh interval constant (28 days)
        private const int CacheRefreshIntervalDays = 28;

        static async Task Main(string[] args)
        {
            LoadConfiguration();

            folderPath = configuration["AppSettings:FolderPath"];
            containerName = configuration["AppSettings:ContainerName"];

            dataJsonPath = Path.Combine(folderPath, "data.json");
            cacheFilePath = Path.Combine(folderPath, "cache.json");

            LoadCache();

            await ProcessChartsAsync();
            SaveCache();
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
            if (cache.ContainsKey(ticker) && cache[ticker].LastUpdated.AddDays(CacheRefreshIntervalDays) >= DateTime.Today)
            {
                return cache[ticker].CompanyName;
            }

            if (cache.ContainsKey(ticker) && refreshCount < 20)
            {
                refreshCount++;
                return await RefreshCompanyNameAsync(ticker);
            }

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
            var lastWriteTime = File.GetLastWriteTime(filePath);

            var chartData = new ChartData
            {
                TickerSymbol = ticker,
                CompanyName = companyName,
                UpdatedDate = lastWriteTime.ToString("yyyy-MM-dd"),
                ChartFilename = $"charts/{chartFileName}",
                Comments = ""
            };

            jsonData.Add(chartData);
        }
    }
}
