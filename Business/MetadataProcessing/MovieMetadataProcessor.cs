using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using PlumMediaCenter.Business.LibraryGeneration.DotJson;
using TMDbLib.Client;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Movies;

namespace PlumMediaCenter.Business.MetadataProcessing
{
    public class MovieMetadataProcessor : BaseManager
    {
        public MovieMetadataProcessor(Manager manager) : base(manager)
        {
        }

        private static TMDbClient Client
        {
            get
            {
                lock (ClientLock)
                {
                    if (_Client == null)
                    {

                        _Client = new TMDbClient(new AppSettings().TmdbApiString);
                        //load default config
                        _Client.GetConfig();
                        //retry a request 10 times.
                        _Client.MaxRetryCount = 10;
                    }
                }
                return _Client;
            }
        }
        private static object ClientLock = new object();
        private static TMDbClient _Client;

        public async Task<List<MovieSearchResult>> GetSearchResults(string text)
        {
            SearchContainer<TMDbLib.Objects.Search.SearchMovie> r;
            try
            {
                //only allow one tmdb request at a time
                lock (Client)
                {
                    r = Client.SearchMovieAsync(text).Result;
                }
            }
            catch (Exception e)
            {
                throw new Exception("TMDB Client was supposed to try again");
            }
            var searchResults = r.Results;
            var result = new List<MovieSearchResult>();
            foreach (var searchResult in searchResults)
            {
                result.Add(new MovieSearchResult
                {
                    Title = searchResult.Title,
                    PosterUrl = Client.GetImageUrl("original", searchResult.PosterPath).ToString(),
                    TmdbId = searchResult.Id,
                    Overview = searchResult.Overview,
                    ReleaseDate = searchResult.ReleaseDate,
                });
            }
            return result;
        }

        public async Task<MovieMetadataComparison> GetComparison(int tmdbId, int movieId, string baseUrl)
        {
            var result = new MovieMetadataComparison();
            var tcurrent = GetCurrentMetadata(movieId, baseUrl);
            var tTmdb = GetTmdbMetadata(tmdbId);

            //convert current posters into tmdb poster urls

            result.Current = await tcurrent;
            result.Incoming = await tTmdb;
            return result;
        }

        public async Task<MovieMetadata> GetTmdbMetadata(int tmdbId)
        {
            Movie movie = null;
            Directory.CreateDirectory(this.Manager.AppSettings.TmdbCacheDirectoryPath);
            var cacheFilePath = $"{this.Manager.AppSettings.TmdbCacheDirectoryPath}{tmdbId}.json";
            //if a cache file exists, and it's was updated less than a month ago, use it.
            if (File.Exists(cacheFilePath) && (DateTime.Now - File.GetLastWriteTime(cacheFilePath)).TotalDays < 30)
            {
                try
                {
                    movie = Newtonsoft.Json.JsonConvert.DeserializeObject<Movie>(File.ReadAllText(cacheFilePath));
                }
                catch (Exception)
                {

                }
            }
            //if the movie could not be loaded from cache, retrieve a fresh copy from TMDB
            if (movie == null)
            {
                //only allow one thread to use the client at a time
                lock (Client)
                {
                    movie = Client.GetMovieAsync(tmdbId,
                       MovieMethods.AlternativeTitles |
                       MovieMethods.Credits |
                       MovieMethods.Images |
                       MovieMethods.Keywords |
                       MovieMethods.Releases |
                       MovieMethods.ReleaseDates |
                       MovieMethods.Videos
                   ).Result;
                }
                //save this result to disc
                var camelCaseFormatter = new JsonSerializerSettings();
                camelCaseFormatter.ContractResolver = new CamelCasePropertyNamesContractResolver();
                camelCaseFormatter.Formatting = Formatting.Indented;

                var json = Newtonsoft.Json.JsonConvert.SerializeObject(movie, camelCaseFormatter);
                await File.WriteAllTextAsync(cacheFilePath, json);
            }

            var metadata = new MovieMetadata();
            metadata.AddCast(movie.Credits?.Cast);
            metadata.AddCrew(movie.Credits?.Crew);
            metadata.Collection = movie.BelongsToCollection?.Name;
            metadata.Description = movie.Overview;
            metadata.Genres = movie.Genres?.Select(x => x.Name).ToList();
            metadata.Keywords = movie.Keywords?.Keywords?.Select(x => x.Name).ToList();
            var release = movie.Releases?.Countries
                ?.Where(x => x.Iso_3166_1.ToLower() == "us")
                ?.OrderBy(x => x.ReleaseDate)
                ?.First();
            //get the oldest US rating
            metadata.Rating = release?.Certification;
            metadata.ReleaseDate = release?.ReleaseDate;
            metadata.Runtime = movie.Runtime;
            metadata.Summary = movie.Overview;
            metadata.Title = movie.Title;

            metadata.Titles.Add(movie.Title);
            metadata.Titles.AddRange(
                movie.AlternativeTitles?.Titles?.Where(x => x.Iso_3166_1.ToLower() == "us").Select(x => x.Title).ToList() ?? new List<string>()
            );
            metadata.Titles = metadata.Titles.Distinct().ToList();

            metadata.TmdbId = movie.Id;


            if (movie.PosterPath != null)
            {
                metadata.PosterUrls.Add(Client.GetImageUrl("original", movie.PosterPath).ToString());
            }
            metadata.PosterUrls.AddRange(
                movie.Images?.Posters
                ?.Where(x => x.Iso_639_1?.ToLower() == "en")
                ?.Select(x => Client.GetImageUrl("original", x.FilePath).ToString())
                ?.ToList() ?? new List<string>()
            );
            metadata.PosterUrls = metadata.PosterUrls.Distinct().ToList();


            //add the marked backdrop path first
            if (movie.BackdropPath != null)
            {
                metadata.BackdropUrls.Add(Client.GetImageUrl("original", movie.BackdropPath).ToString());
            }
            //add all additional backdrops
            metadata.BackdropUrls.AddRange(
                movie.Images?.Backdrops
                //move the highest rated backdrops to the top
                ?.OrderByDescending(x => x.VoteAverage)
                ?.Where(x => x.Iso_639_1?.ToLower() == "en" || x.Iso_639_1 == null)
                ?.Select(x => Client.GetImageUrl("original", x.FilePath).ToString())
                ?.ToList() ?? new List<string>()
            );
            metadata.BackdropUrls = metadata.BackdropUrls.Distinct().ToList();
            return metadata;
        }

        private async Task<MovieMetadata> GetCurrentMetadata(int movieId, string baseUrl)
        {
            var movieModel = await this.Manager.Movies.GetById(movieId);
            var movie = new LibraryGeneration.Movie(this.Manager, movieModel.GetFolderPath(), movieModel.SourceId);
            //throw new Exception(Newtonsoft.Json.JsonConvert.SerializeObject(movie.MovieDotJson));
            var metadata = new MovieMetadata(movie.MovieDotJson);

            //if the movie has a poster, add its local url
            var posterPath = $"{movie.FolderPath}/poster.jpg";
            if (File.Exists(posterPath))
            {
                var name = Path.GetFileName(posterPath);
                metadata.PosterUrls.Add($"{movieModel.FolderUrl}{name}");
            }

            //get all backdrops listed in movie.json
            var backdrops = movie.MovieDotJson?.Backdrops ?? new List<Image>();

            //get all backdrops from filesystem, and include only those not already listed in the movie.json
            var backdropsFromFs = Directory.Exists(movie.BackdropFolderPath) ? Directory.GetFiles(movie.BackdropFolderPath) : new string[0];
            foreach (var backdropPath in backdropsFromFs)
            {
                var backdropFilename = Path.GetFileName(backdropPath);
                var backdropAlreadyListed = backdrops.Where(x =>
                {
                    return Path.GetFileName(x.Path) == backdropFilename;
                }).Count() > 0;

                if (backdropAlreadyListed == false)
                {
                    var relativeBackdropPath = $"backdrops/{backdropFilename}";
                    backdrops.Add(new Image { Path = relativeBackdropPath });
                }
            }

            foreach (var backdrop in backdrops)
            {
                //add the source url as is
                if (backdrop.SourceUrl != null)
                {
                    metadata.BackdropUrls.Add(backdrop.SourceUrl);
                }
                else
                {
                    //the backdrop doesn't have a source url...so assume it's a locally added image. add the local url
                    var path = $"{movie.FolderPath}/{backdrop.Path}";
                    if (File.Exists(path))
                    {
                        var name = Path.GetFileName(path);
                        metadata.BackdropUrls.Add($"{movieModel.FolderUrl}{backdrop.Path}");
                    }
                }
            }
            return metadata;
        }

        public async Task Save(int movieId, MovieMetadata metadata)
        {
            var movie = await this.Manager.Movies.GetById(movieId);
            await DownloadMetadata(movie.GetFolderPath(), movie.FolderUrl, metadata);
            //reprocess this movie so the library is updated with its info
            await this.Manager.LibraryGeneration.Movies.Process(movie.GetFolderPath());
        }

        public async Task DownloadMetadata(string movieFolderPath, string movieFolderUrl, MovieMetadata metadata)
        {
            //process the poster
            {
                var posterPath = $"{movieFolderPath}poster.jpg";

                if (metadata.PosterUrls.Count == 0)
                {
                    if (File.Exists(posterPath))
                    {
                        File.Delete(posterPath);
                    }
                }
                else
                {
                    //only keep the first poster, since we only store a single poster
                    new WebClient().DownloadFile(metadata.PosterUrls.First(), posterPath);
                }
            }

            //copy the backdrops
            CopyBackdrops(metadata, movieFolderUrl, movieFolderPath);

            var movieDotJsonPath = $"{movieFolderPath}movie.json";
            var movieDotJson = new MovieDotJson(metadata);

            var camelCaseFormatter = new JsonSerializerSettings();
            camelCaseFormatter.ContractResolver = new CamelCasePropertyNamesContractResolver();
            camelCaseFormatter.Formatting = Formatting.Indented;

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(movieDotJson, camelCaseFormatter);
            await File.WriteAllTextAsync(movieDotJsonPath, json);

        }
        public List<string> CopyBackdrops(MovieMetadata metadata, Models.Movie movie, string moviePath)
        {
            return this.CopyBackdrops(metadata, movie.FolderUrl, moviePath);
        }

        /// <summary>
        /// Copy/download a set of images to the destination path, removing any images from destination that are not in the list.
        /// Returns a list of image paths for the newly copied files
        /// </summary>
        /// <param name="imageUrls"></param>
        /// <param name="destinationPath"></param>
        /// <returns></returns>
        public List<string> CopyBackdrops(MovieMetadata metadata, string movieFolderUrl, string moviePath)
        {
            var destinationPath = Utility.NormalizePath($"{moviePath}backdrops/", false);
            var tempPaths = new List<string>();
            Directory.CreateDirectory(AppSettings.TempPath);
            var backdropUrlsToProcess = new List<string>();

            var originalBackdrops = metadata.Backdrops;
            metadata.Backdrops = new List<Image>();

            //exclude any backdrops that we already have
            foreach (var imageUrl in metadata.BackdropUrls)
            {
                var image = originalBackdrops.Where(x => x.SourceUrl == imageUrl).FirstOrDefault();
                var imagePath = image?.Path == null ? null : Utility.NormalizePath($"{moviePath}{image.Path}", true);
                //if this image originated from this url, store a basic image record in the json
                if (string.IsNullOrWhiteSpace(this.Manager.BaseUrl) == false &&
                    imageUrl.ToLowerInvariant().Contains(this.Manager.BaseUrl.ToLowerInvariant()))
                {
                    var len = imageUrl.Length - imageUrl.ToLowerInvariant().Replace(movieFolderUrl.ToLowerInvariant(), "").Length;
                    var relativePath = imageUrl.Substring(len);

                    metadata.Backdrops.Add(new Image { Path = relativePath });
                }
                //if we don't have reference to this image in the json, or the image doesn't exist on disc, process it
                else if (image == null || File.Exists(imagePath) == false)
                {
                    //store the backdrop in the list of backdrops (to maintain sort order). This record will be updated
                    //with a filename later in the process
                    metadata.Backdrops.Add(new Image { SourceUrl = imageUrl });
                    backdropUrlsToProcess.Add(imageUrl);
                }
                else
                {
                    //keep the existing image
                    metadata.Backdrops.Add(image);
                }
            }

            //download the new posters
            foreach (var imageUrl in backdropUrlsToProcess)
            {
                var ext = Path.GetExtension(imageUrl);
                var filename = $"{Guid.NewGuid().ToString()}{ Path.GetExtension(imageUrl)}";
                var tempImagePath = $"{AppSettings.TempPath}/{filename}";
                var client = new WebClient();
                Directory.CreateDirectory(AppSettings.TempPath);
                client.DownloadFile(imageUrl, tempImagePath);
                tempPaths.Add(tempImagePath);

                //update metadata with backdrop filename
                var imageFromJson = metadata.Backdrops.Where(x => x.SourceUrl == imageUrl).FirstOrDefault();
                imageFromJson.Path = Utility.NormalizePath($"backdrops/{filename}", true);
            }
            //make the backdrop folder in the movie folder
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));

            var imagePaths = new List<string>();
            //copy all of the temp posters into the backdrops folder
            foreach (var tempImagePath in tempPaths)
            {
                var filename = Path.GetFileName(tempImagePath);
                var imagePath = $"{destinationPath}{filename}";
                //copy the image to the destination
                File.Copy(tempImagePath, imagePath);
                //delete the temp image
                File.Delete(tempImagePath);
                imagePaths.Add(imagePath);
            }
            return imagePaths;
        }
    }

    public class MovieMetadataComparison
    {
        public MovieMetadata Incoming;
        public MovieMetadata Current;
    }
    public class MovieMetadata : MovieDotJson
    {
        public MovieMetadata()
        {

        }
        public MovieMetadata(MovieDotJson metadata)
        {
            if (metadata == null)
            {
                return;
            }
            var t = metadata.GetType();
            var myType = this.GetType();
            var properties = t.GetProperties();
            //set all of the metadata properties to this
            foreach (var prop in properties)
            {
                var value = prop.GetValue(metadata);
                myType.GetProperty(prop.Name).SetValue(this, value);
            }
        }
        public List<string> PosterUrls = new List<string>();
        public List<string> BackdropUrls = new List<string>();
        public void AddCast(List<Cast> cast)
        {
            if (cast == null)
            {
                return;
            }
            foreach (var member in cast)
            {
                this.Cast.Add(new CastMember
                {
                    Character = member.Character,
                    Name = member.Name,
                    TmdbId = member.Id
                });
            }
        }
        public void AddCrew(List<Crew> crew)
        {
            if (crew == null)
            {
                return;
            }
            foreach (var member in crew)
            {
                this.Crew.Add(new CrewMember
                {
                    Job = member.Job,
                    Name = member.Name,
                    TmdbId = member.Id
                });
            }
        }
    }

    public class MovieSearchResult
    {
        public string Title;
        public string PosterUrl;
        public int TmdbId;
        public string Overview;
        public DateTime? ReleaseDate;
    }
}