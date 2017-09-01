using System.Collections.Generic;
using System.Threading.Tasks;
using PlumMediaCenter.Data;

namespace PlumMediaCenter.Business.LibraryGeneration.Managers
{
    public class TvSerieManager : BaseManager
    {
        public TvSerieManager(Manager manager) : base(manager)
        {

        }

        /// <summary>
        /// Get a list of every show directory
        /// </summary>
        /// <returns></returns>
        public async Task<List<string>> GetDirectories()
        {
            var task = Task.FromResult(new List<string>());
            return await task;
        }
    }
}