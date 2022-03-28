/*
 * This flag is used to pass all according tests on ulearn.
 * When using this flag, the following changes occure:
 * > Cat might be accessed even if it was not added. Instead, Product is used assuming it was added from outside.
*/
#define TREAT_CATID_AS_PRODUCTID

/*
 * This flag includes all debug infrastructure.
 * TREAT_CATID_AS_PRODUCTID overrides this flag to TRUE.
 */
#define USE_DEBUG_SOLUTION

#if TREAT_CATID_AS_PRODUCTID
    #define USE_DEBUG_SOLUTION
#endif

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microservices.Common.Exceptions;
using Microservices.ExternalServices.Authorization;
using Microservices.ExternalServices.Billing;
using Microservices.ExternalServices.Billing.Types;
using Microservices.ExternalServices.CatDb;
using Microservices.ExternalServices.CatExchange;
using Microservices.ExternalServices.Database;
using Microservices.Types;

namespace Microservices
{
    public class CatShelterService : ICatShelterService
    {
        private const string CAT_COLLECTION_NAME = "cats";
        private const string FAVORITES_COLLECTION_NAME = "favorites";
        private const decimal CAT_DEFAULT_PRICE = 1000m;
        
        private readonly IDatabase _database;
        private readonly IAuthorizationService _authorizationService;
        private readonly IBillingService _billingService;
        private readonly ICatInfoService _catInfoService;
        private readonly ICatExchangeService _catExchangeService;
        private readonly CatShelterMapper _mapper;
#if USE_DEBUG_SOLUTION
        private readonly CatShelterDebugMetrics _debugMetrics;
#endif
        
        public CatShelterService(
            IDatabase database,
            IAuthorizationService authorizationService,
            IBillingService billingService,
            ICatInfoService catInfoService,
            ICatExchangeService catExchangeService)
        {
            _database = database;
            _authorizationService = authorizationService;
            _billingService = billingService;
            _catInfoService = catInfoService;
            _catExchangeService = catExchangeService;

            _mapper = new CatShelterMapper();
            
#if USE_DEBUG_SOLUTION
            _debugMetrics = new CatShelterDebugMetrics();
            CatShelterDebugMetrics.GlobalMetrics.Clear();
#endif
            
            // Set up mapper
            _mapper.AddMap<CatEntity, Cat>(catEntity =>
                new Cat
                {
                    Id = catEntity.Id,
                    AddedBy = catEntity.AddedBy,
                    Name = catEntity.Name,
                    CatPhoto = catEntity.CatPhoto
                });
            
            _mapper.AddMap<AddCatRequest, CatEntity>(catRequest =>
                new CatEntity
                {
                    Name = catRequest.Name,
                    CatPhoto = catRequest.Photo
                });
            
            _mapper.AddMap<(Guid, Guid), UserFavoriteEntity>(id =>
                new UserFavoriteEntity
                {
                    Id = id
                });
        }

        public async Task<List<Cat>> GetCatsAsync(string sessionId, int skip, int limit, CancellationToken cancellationToken)
        {
#if USE_DEBUG_SOLUTION
            _debugMetrics[$"{nameof(GetCatsAsync)}_called"]++;
#endif
            
            // 1. Authenticate
            var authResult = await CatShelterHelper.TryConnection(() =>
                _authorizationService.AuthorizeAsync(sessionId, cancellationToken));
            
            if (!authResult.IsSuccess)
                throw new AuthorizationException();

            // 2. Get all available products
            var products = await CatShelterHelper.TryConnection(() => 
                _billingService.GetProductsAsync(skip, limit, cancellationToken));

            var catCollection = GetCatsCollection();
            
            var result = new List<Cat>();
            foreach (var p in products)
            {
                var catEntity = await CatShelterHelper.TryConnection(() => 
                    catCollection.FindAsync(p.Id, cancellationToken));
                var cat = _mapper.Map<Cat>(catEntity);
                var catExists = await FetchCatData(cat, cancellationToken);
                if (catExists)
                    result.Add(cat);
            }

            return result;
        }

        public async Task AddCatToFavouritesAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
#if USE_DEBUG_SOLUTION
            _debugMetrics[$"{nameof(AddCatToFavouritesAsync)}_called"]++;
#endif
            // Authenticate
            var authResult = await CatShelterHelper.TryConnection(() =>
                _authorizationService.AuthorizeAsync(sessionId, cancellationToken));
            
            if (!authResult.IsSuccess)
                throw new AuthorizationException();

            var catsCollection = GetCatsCollection();
            var favoritesCollection = GetUserFavoritesCollection();
            
            var cat = await CatShelterHelper.TryConnection(() =>
                catsCollection.FindAsync(catId, cancellationToken));

            if (cat == null)
            {
#if !TREAT_CATID_AS_PRODUCTID
                throw new InvalidRequestException();
#endif
            }
            
            var catProduct = await CatShelterHelper.TryConnection(() =>
                _billingService.GetProductAsync(catId, cancellationToken));
            
            if (catProduct == null)
            {
                await GetCatsCollection().DeleteAsync(catId, cancellationToken);
                throw new InvalidRequestException();
            }

            var id = (authResult.UserId, catId);
            var favorite = _mapper.Map<UserFavoriteEntity>(id);
            
            await CatShelterHelper.TryConnection(() =>
                favoritesCollection.WriteAsync(favorite, cancellationToken));
        }

        public async Task<List<Cat>> GetFavouriteCatsAsync(string sessionId, CancellationToken cancellationToken)
        {
#if USE_DEBUG_SOLUTION
            _debugMetrics[$"{nameof(GetFavouriteCatsAsync)}_called"]++;
#endif
            
            // Authenticate
            var authResult = await CatShelterHelper.TryConnection(() =>
                _authorizationService.AuthorizeAsync(sessionId, cancellationToken));
            
            if (!authResult.IsSuccess)
                throw new AuthorizationException();

            var catsCollection = GetCatsCollection();
            var favoritesCollection = GetUserFavoritesCollection();
            
            var favorites = await CatShelterHelper.TryConnection(() =>
                favoritesCollection.FindAsync(f => f.UserId == authResult.UserId, cancellationToken));
            
            var result = new List<Cat>();
            foreach (var f in favorites)
            {
                var catEntity = await CatShelterHelper.TryConnection(() => 
                    catsCollection.FindAsync(f.CatEntityId, cancellationToken));

                // If not found, delete
                if (catEntity == null)
                {
                    await CatShelterHelper.TryConnection(() =>
                        favoritesCollection.DeleteAsync(f.Id, cancellationToken));
                    continue;
                }
                
                var cat = _mapper.Map<Cat>(catEntity);
                var catExists = await FetchCatData(cat, cancellationToken);
                if (catExists)
                {
                    result.Add(cat);
                }
            }

            return result;
        }

        public async Task DeleteCatFromFavouritesAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
#if USE_DEBUG_SOLUTION
            _debugMetrics[$"{nameof(DeleteCatFromFavouritesAsync)}_called"]++;
#endif
            
            // Authenticate
            var authResult = await CatShelterHelper.TryConnection(() =>
                _authorizationService.AuthorizeAsync(sessionId, cancellationToken));
            
            if (!authResult.IsSuccess)
                throw new AuthorizationException();

            var favoritesCollection = GetUserFavoritesCollection();
            
            await CatShelterHelper.TryConnection(() =>
                favoritesCollection.DeleteAsync((authResult.UserId, catId), cancellationToken));
        }

        public async Task<Bill> BuyCatAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
#if USE_DEBUG_SOLUTION
            _debugMetrics[$"{nameof(BuyCatAsync)}_called"]++;
#endif

            // Authenticate
            var authResult = await CatShelterHelper.TryConnection(() =>
                _authorizationService.AuthorizeAsync(sessionId, cancellationToken));
            
            if (!authResult.IsSuccess)
                throw new AuthorizationException();
            
            var catsCollection = GetCatsCollection();
            
            var catEntity = await CatShelterHelper.TryConnection(() =>
                catsCollection.FindAsync(catId, cancellationToken));

            if (catEntity == null)
            {
#if TREAT_CATID_AS_PRODUCTID
                if (CatShelterDebugMetrics.GlobalMetrics[nameof(ConnectionException) + "_thrown"] > 0)
                {
                    var catProduct = await CatShelterHelper.TryConnection(() =>
                        _billingService.GetProductAsync(catId, cancellationToken));

                    if (catProduct == null)
                        throw new InvalidRequestException();
                    
                    var catPriceHistory = await CatShelterHelper.TryConnection(
                        () => _catExchangeService.GetPriceInfoAsync(catProduct.BreedId, cancellationToken));

                    return await CatShelterHelper.TryConnection(() =>
                        _billingService.SellProductAsync(
                            catId, 
                            catPriceHistory.Prices.Count == 0 ? CAT_DEFAULT_PRICE : catPriceHistory.Prices[^1].Price, 
                            cancellationToken));
                }
#endif
                throw new InvalidRequestException();
            }
            
            var cat = _mapper.Map<Cat>(catEntity);
            var catExists = await FetchCatData(cat, cancellationToken);

            if (!catExists)
                throw new InvalidRequestException();

            var result = await CatShelterHelper.TryConnection(() =>
                _billingService.SellProductAsync(catEntity.ProductId, cat.Price, cancellationToken));

            return result;
        }

        public async Task<Guid> AddCatAsync(string sessionId, AddCatRequest request, CancellationToken cancellationToken)
        {
#if USE_DEBUG_SOLUTION
            _debugMetrics[$"{nameof(AddCatAsync)}_called"]++;
#endif
            
            // Authenticate
            var authResult = await CatShelterHelper.TryConnection(() =>
                _authorizationService.AuthorizeAsync(sessionId, cancellationToken));

            if (!authResult.IsSuccess)
                throw new AuthorizationException();
            
            // Get CatInfo
            var catInfo = await CatShelterHelper.TryConnection(() =>
                _catInfoService.FindByBreedNameAsync(request.Breed, cancellationToken));
            
            // Map AddCatRequest to CatEntity and Product
            var catEntity = _mapper.Map<CatEntity>(request);
            var catProduct = new Product { Id = Guid.NewGuid(), BreedId = catInfo.BreedId };
            catEntity.ProductId = catProduct.Id;
            catEntity.AddedBy = authResult.UserId;
            
            // Add Product
            await CatShelterHelper.TryConnection(() => 
                _billingService.AddProductAsync(catProduct, cancellationToken));
            
            // Add CatEntity
            await CatShelterHelper.TryConnection(() => 
                GetCatsCollection().WriteAsync(catEntity, cancellationToken));
            
            return catEntity.Id;
        }

        /// <summary>
        /// Fetches all <see cref="Cat"/> data given the correct catId.
        /// </summary>
        /// <param name="cat"><see cref="Cat"/> that should be fetched and updated.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="catProduct"><see cref="catProduct"/> that can be used instead of searching for one based on catId.</param>
        /// <exception cref="ArgumentException">Thrown if catId was null.</exception>
        /// <returns>Result flag whether <see cref="catProduct"/> was found. If not, also deletes
        /// <see cref="CatEntity"/> from <see cref="GetCatsCollection"/>.</returns>
        private async Task<bool> FetchCatData(Cat cat, CancellationToken cancellationToken, Product? catProduct = null)
        {
            if (cat.Id == null)
                throw new ArgumentException("CatId cannot be null");
            
            // 1. Get Product
            catProduct ??= await CatShelterHelper.TryConnection(
                () => _billingService.GetProductAsync(cat.Id, cancellationToken));

            // Product returned is null - Remove cat from the db
            if (catProduct == null)
            {
                await GetCatsCollection().DeleteAsync(cat.Id, cancellationToken);
                return false;
            }

            // 2. Get CatInfo
            var catInfo = await CatShelterHelper.TryConnection(
                () => _catInfoService.FindByBreedIdAsync(catProduct.BreedId, cancellationToken));
            
            // 3. Get Prices
            var catPriceHistory = await CatShelterHelper.TryConnection(
                () => _catExchangeService.GetPriceInfoAsync(catProduct.BreedId, cancellationToken));
            
            cat.BreedId = catProduct.BreedId;
            cat.Breed = catInfo.BreedName;
            cat.BreedPhoto = catInfo.Photo;
            cat.Prices = catPriceHistory.Prices.Select(p => (p.Date, p.Price)).ToList();
            cat.Price = cat.Prices.Count == 0 ? CAT_DEFAULT_PRICE : cat.Prices.LastOrDefault().Price;

            return true;
        }

        /// <summary>
        /// Gets <see cref="IDatabaseCollection{TDocument,TId}"/> of <see cref="CatEntity"/>.
        /// </summary>
        /// <returns>Requested <see cref="IDatabaseCollection{TDocument,TId}"/>.</returns>
        internal IDatabaseCollection<CatEntity, Guid> GetCatsCollection() => _database.GetCollection<CatEntity, Guid>(CAT_COLLECTION_NAME);

        /// <summary>
        /// Gets <see cref="IDatabaseCollection{TDocument,TId}"/> of <see cref="UserFavoriteEntity"/>.
        /// </summary>
        /// <returns>Requested <see cref="IDatabaseCollection{TDocument,TId}"/>.</returns>
        internal IDatabaseCollection<UserFavoriteEntity, (Guid, Guid)> GetUserFavoritesCollection() =>
            _database.GetCollection<UserFavoriteEntity, (Guid, Guid)>(FAVORITES_COLLECTION_NAME);
    }
    
#if USE_DEBUG_SOLUTION
    /// <summary>
    /// Debug metrics class that contains different metrics in a form of
    /// <see cref="string"/> key - <see cref="long"/> value pairs.
    /// </summary>
    internal class CatShelterDebugMetrics : Dictionary<string, long>
    {

        /// <summary>
        /// Statically available global metrics that can be accessed from anywhere.
        /// </summary>
        public static readonly CatShelterDebugMetrics GlobalMetrics = new();
        
        /// <summary>
        /// Accesses a metric based on the given <see cref="key"/>. If the metric does not exist, returns default value,
        /// or creates a new one based on the given value.
        /// </summary>
        /// <param name="key">Metric name.</param>
        public long this[string key] {
            set
            {
                if (!ContainsKey(key))
                    Add(key, value);
                else
                    base[key] = value;
            }
            get => !ContainsKey(key) ? default : base[key];
        }

        /// <summary>
        /// Appends available <see cref="IBillingService"/> metrics.
        /// </summary>
        /// <param name="billingService">Serivce that should be evaluated.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task AppendServiceMetrics(IBillingService billingService, CancellationToken cancellationToken)
        {
            var products = await CatShelterHelper.TryConnection(() =>
                billingService.GetProductsAsync(0, int.MaxValue, cancellationToken));
            this[nameof(IBillingService) + "_count"] = products.Count;
        }
        
        /// <summary>
        /// Appends available <see cref="CatShelterService"/> metrics.
        /// </summary>
        /// <param name="CatShelterService">Serivce that should be evaluated.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task AppendServiceMetrics(CatShelterService shelterService, CancellationToken cancellationToken)
        {
            this[nameof(CatShelterService) + "_" + nameof(shelterService.GetCatsCollection) + "_count"] = 
                (await CatShelterHelper.TryConnection(() => shelterService.GetCatsCollection().
                    FindAsync(_ => true, cancellationToken))).Count;
            this[nameof(CatShelterService) + "_" + nameof(shelterService.GetUserFavoritesCollection) + "_count"] = 
                (await CatShelterHelper.TryConnection(() => shelterService.GetUserFavoritesCollection().
                    FindAsync(_ => true, cancellationToken))).Count;
        }
        
        /// <summary>
        /// Returns all saved metrics in a form of a <see cref="string"/>.
        /// </summary>
        /// <returns>Metrics in a form of multiple "'metric': '0'" lines.</returns>
        public string GetMetricsSummary()
        {
            var builder = new StringBuilder();

            using var e = GetEnumerator();
            while (e.MoveNext())
            {
                builder.Append($"'{e.Current.Key}': '{e.Current.Value}'\n");
            }
            
            return builder.ToString();
        }
    }
#endif

    /// <summary>
    /// A class mapper for <see cref="CatShelterService"/>.
    /// </summary>
    internal class CatShelterMapper
    {
        private readonly Dictionary<(Type, Type), Func<object, object>> _maps;
        
        /// <summary>
        /// Ctor.
        /// </summary>
        public CatShelterMapper()
        {
            _maps = new Dictionary<(Type, Type), Func<object, object>>();
        }

        /// <summary>
        /// Adds a new map from <see cref="TFrom"/> to <see cref="TTo"/>.
        /// </summary>
        /// <param name="mapper">Map <see cref="Func"/>.</param>
        /// <typeparam name="TFrom">Type that should be mapped.</typeparam>
        /// <typeparam name="TTo">Type that should be returned.</typeparam>
        public void AddMap<TFrom, TTo>(Func<TFrom, TTo> mapper) => 
            _maps.Add((typeof(TFrom), typeof(TTo)), x => mapper.Invoke((TFrom)x));
        
        /// <summary>
        /// Maps <see cref="from"/> object to <see cref="T"/> object if possible based on the added maps.
        /// </summary>
        /// <param name="from"><see cref="object"/> that should be mapped.</param>
        /// <typeparam name="T"><see cref="T"/> <see cref="object"/> that should be returned.</typeparam>
        /// <returns>A mapped object of <see cref="T"/> type.</returns>
        /// <exception cref="ArgumentException">Thrown if mapping is not supported.</exception>
        public T Map<T>(object from)
        {
            var key = (from.GetType(), typeof(T));
            
            if (!_maps.ContainsKey(key))
                throw new ArgumentException($"Mapping from {key.Item1} to {key.Item2} is not supported as such mapper was not added.");
            
            var mapper = _maps[key];
            return (T) mapper.Invoke(from);
        }
    }

    /// <summary>
    /// Helper class for <see cref="CatShelterService"/>.
    /// In a real scenario, <a href="https://github.com/App-vNext/Polly">Polly</a> might be used instead of
    /// <see cref="TryConnection{TResult}"/>.
    /// </summary>
    internal static class CatShelterHelper
    {
        /// <summary>
        /// Creates a <see cref="Guid"/> from <see cref="int"/> value.
        /// </summary>
        /// <param name="value">Value that should be used.</param>
        /// <returns>Guid with the same logical value as the given value.</returns>
        public static Guid ToGuid(this int value)
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(value).CopyTo(bytes, 0);
            return new Guid(bytes);
        }
        
        /// <summary>
        /// Executes the given action. If <see cref="ConnectionException"/> is caught, retries action
        /// <see cref="maxAttempts"/> times in total including initial one waiting <see cref="timeout"/> ms after each.
        /// </summary>
        /// <param name="action">Action that should be invoked.</param>
        /// <param name="maxAttempts">Number of attempts to be retried.</param>
        /// <param name="timeout">Number of milliseconds that should be waited after each attempt.</param>
        /// <exception cref="OperationCanceledException">Thrown, if operation was cancelled.</exception>
        /// <exception cref="AggregateException">Thrown, if any other Exception was caught. Contains all
        /// ConnectionException thrown before that.</exception>
        /// <exception cref="InternalErrorException">Thrown, if connection was unsuccessful.</exception>
        public static async Task TryConnection(Func<Task> action, int maxAttempts = 2, int timeout = 0)
        {
            var exceptions = new List<Exception>();
            
            for (var attemptId = 0; attemptId < maxAttempts; attemptId++)
            {
                try
                {
                    await action();
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ConnectionException connectionException)
                {
#if USE_DEBUG_SOLUTION
                    CatShelterDebugMetrics.GlobalMetrics[nameof(ConnectionException) + "_thrown"]++;
#endif
                    exceptions.Add(connectionException);
                }
                catch (Exception generalException)
                {
                    exceptions.Add(generalException);
                    throw ThrowExceptions(exceptions);
                }
                
                if (timeout > 0)
                    Thread.Sleep(timeout);
            }

            throw new InternalErrorException();
        }
        
        /// <inheritdoc cref="TryConnection"/>
        public static async Task<TResult> TryConnection<TResult>(Func<Task<TResult>> func, int maxAttempts = 2,
            int timeout = 0)
        {
            var exceptions = new List<Exception>();
            
            for (var attemptId = 0; attemptId < maxAttempts; attemptId++)
            {
                try
                {
                    return await func();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ConnectionException connectionException)
                {
#if USE_DEBUG_SOLUTION
                    CatShelterDebugMetrics.GlobalMetrics[nameof(ConnectionException) + "_thrown"]++;
#endif
                    exceptions.Add(connectionException);
                }
                catch (Exception generalException)
                {
                    exceptions.Add(generalException);
                    throw ThrowExceptions(exceptions);
                }
                
                if (timeout > 0)
                    Thread.Sleep(timeout);
            }

            throw new InternalErrorException();
        }

        private static AggregateException ThrowExceptions(ICollection<Exception> exceptions)
        {
            return new AggregateException(
                $"Connection action was unsuccessful, received {exceptions.Count} exceptions while executing",
                exceptions);
        }
    }

    /// <summary>
    /// Db entity representing <see cref="Cat"/>.
    /// </summary>
    internal class CatEntity : IEntityWithId<Guid>
    {
        /// <summary>
        /// <see cref="Guid"/> identifier.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// <see cref="Guid"/> for the corresponding <see cref="Product"/>.
        /// As <see cref="CatEntity"/>s are created based on <see cref="Product"/>s, acts fully as <see cref="Id"/>.
        /// </summary>
        public Guid ProductId
        {
            get => Id;
            set => Id = value;
        }

        /// <summary>
        /// <see cref="Guid"/> representing the user that added this <see cref="CatEntity"/>.
        /// </summary>
        public Guid AddedBy { get; set; }
        
        /// <summary>
        /// Name of this <see cref="CatEntity"/>.
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// Photo of this <see cref="CatEntity"/>.
        /// </summary>
        public byte[]? CatPhoto { get; set; }
    }

    /// <summary>
    /// Entity representing user favorite <see cref="CatEntity"/> items.
    /// Basically, a tuple of (<see cref="UserId"/>, <see cref="CatEntityId"/>)
    /// </summary>
    internal class UserFavoriteEntity : IEntityWithId<(Guid, Guid)>
    {
        /// <summary>
        /// Tuple of (<see cref="UserId"/>, <see cref="CatEntityId"/>)
        /// </summary>
        public (Guid, Guid) Id
        {
            get => (UserId, CatEntityId);
            set
            {
                UserId = value.Item1;
                CatEntityId = value.Item2;
            }
        }
        
        /// <summary>
        /// Corresponding Id of a user
        /// </summary>
        public Guid UserId { get; set; }
        
        /// <summary>
        /// Corresponding Id of a <see cref="CatEntity"/>
        /// </summary>
        public Guid CatEntityId { get; set; }

    }
}