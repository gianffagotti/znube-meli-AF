using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace meli_znube_integration
{
    public class HealthFunction
    {
        [Function("Health")] 
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
        {
            var res = req.CreateResponse(HttpStatusCode.OK);
            await res.WriteStringAsync("OK", Encoding.UTF8);
            return res;
        }
    }
}


