﻿using System;
using GSMA.MobileConnect.Demo.Config;
using GSMA.MobileConnect.Cache;
using GSMA.MobileConnect.Discovery;
using GSMA.MobileConnect.Utils;
using GSMA.MobileConnect.Web;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Results;
using GSMA.MobileConnect.Constants;
using GSMA.MobileConnect.ServerSide.Web.Objects;
using GSMA.MobileConnect.ServerSide.Web.Utils;
using Newtonsoft.Json;

namespace GSMA.MobileConnect.ServerSide.Web.Controllers
{
    [RoutePrefix("server_side_api")]
    public class MobileConnectController : ApiController
    {
        private static ReadAndParseFiles readAndParseFiles = new ReadAndParseFiles();
        private static MobileConnectWebInterface _mobileConnect;
        private static string _apiVersion;
        private static bool _includeRequestIP;
        private static RestClient _restClient;
        private static ICache _cache;
        private static MobileConnectConfig _mobileConnectConfig;
        private static OperatorUrls _operatorUrLs;
        private static OperatorParameters _operatorParams;
        private static CachedParameters CachedParameters = new CachedParameters();
        private static ResponseChecker responseChecker = new ResponseChecker();

        public MobileConnectController(MobileConnectWebInterface mobileConnect)
        {
            _mobileConnect = mobileConnect;
        }

        public MobileConnectController()
        {
            var cache = new ConcurrentCache();
            if (_mobileConnect == null)
                _mobileConnect = new MobileConnectWebInterface(DemoConfiguration.Config, cache);
        }

        [HttpGet]
        [Route("start_discovery")]
        public async Task<string> StartDiscovery(string msisdn = "", string mcc = "", string mnc = "", string sourceIp = "")
        {
            GetParameters();
            var requestOptions = new MobileConnectRequestOptions { ClientIP = sourceIp };
            var status = await _mobileConnect.AttemptDiscoveryAsync(Request, msisdn, mcc, mnc, true, _includeRequestIP, requestOptions);
            CachedParameters.sdkSession = status.SDKSession;
            var authResponse = await StartAuthentication(status.SDKSession, status.DiscoveryResponse.ResponseData.subscriber_id);
           
            return authResponse;
        }

        [HttpGet]
        [Route("headless_authentication")]
        public async Task<IHttpActionResult> RequestHeadlessAuthentication(string sdksession = null, string subscriberId = null, string scope = null)
        {
            var options = new MobileConnectRequestOptions
            {
                Scope = scope,
                Context = "headless",
                BindingMessage = "demo headless",
                AutoRetrieveIdentityHeadless = true,
            };

            var response = await _mobileConnect.RequestHeadlessAuthenticationAsync(Request, sdksession, subscriberId, null, null, options);
            return CreateResponse(response);
        }

        [HttpGet]
        [Route("")]
        public async Task<IHttpActionResult> HandleRedirect(string sdksession = null, string mcc_mnc = null, string code = null, string expectedState = null, string expectedNonce = null)
        {
            // Accept valid results and results indicating validation was skipped due to missing support on the provider
            var requestOptions = new MobileConnectRequestOptions { AcceptedValidationResults = Authentication.TokenValidationResult.Valid | Authentication.TokenValidationResult.IdTokenValidationSkipped };
            var response = await _mobileConnect.HandleUrlRedirectAsync(Request, Request.RequestUri, sdksession, expectedState, expectedNonce, requestOptions);

            return CreateResponse(response);
        }

        [HttpGet]
        [Route("discovery_callback")]
        public async Task<IHttpActionResult> DiscoveryCallback(string state)
        {
            var requestOptions = new MobileConnectRequestOptions { AcceptedValidationResults = Authentication.TokenValidationResult.Valid | Authentication.TokenValidationResult.IdTokenValidationSkipped };
            var cachedInfo = responseChecker.getData(state);
            var authConnectStatus = await _mobileConnect.HandleUrlRedirectAsync(Request, Request.RequestUri, cachedInfo.Result.sdkSession, state, cachedInfo.Result.nonce, requestOptions);
            MobileConnectStatus response = null;
            var idTokenResponseModel = JsonConvert.DeserializeObject<IdTokenResponse>(authConnectStatus.TokenResponse.DecodedIdTokenPayload);
            if (idTokenResponseModel.nonce.Equals(cachedInfo.Result.nonce))
            {
                if (_operatorParams.identity == "True")
                {
                    response = await RequestUserInfo(state, authConnectStatus.TokenResponse.ResponseData.AccessToken);
                    return CreateIdentityResponse(response, authConnectStatus);
                }
                else if (_operatorParams.userInfo == "True")
                {
                    response = await RequestIdentity(state, authConnectStatus.TokenResponse.ResponseData.AccessToken);
                    return CreateIdentityResponse(response, authConnectStatus);
                }
            }
            else
            {
                response = MobileConnectStatus.Error(ErrorCodes.InvalidArgument, "nonce is incorrect", new Exception());
            }
            return CreateResponse(response);
        }

        public async Task<MobileConnectStatus> RequestUserInfo(string state = null, string accessToken = null)
        {
            var cachedInfo = responseChecker.getData(state);
            MobileConnectStatus response = null;
            try
            {
                response = await _mobileConnect.RequestUserInfoAsync(Request, cachedInfo.Result.sdkSession,
                    accessToken == null ? cachedInfo.Result.accessToken : accessToken, new MobileConnectRequestOptions());
            }
            catch (Exception e)
            {
                if (cachedInfo.Result != null)
                {
                    return response;
                }
                response = MobileConnectStatus.Error(ErrorCodes.InvalidArgument, "state value is incorrect", e);
            }
           
            return response;
        }

        public async Task<string> StartAuthentication(string sdksession = null, string subscriberId = null)
        {
            string scope = _operatorParams.scope;
            if (scope == null && _operatorUrLs.ProviderMetadataUrl == null)
                _apiVersion = Utils.Constants.Version1;
            else if (scope == null)
                _apiVersion = Utils.Constants.Version2;

            var options = new MobileConnectRequestOptions
            {
                Scope = scope,
                Context = _apiVersion.Equals(Utils.Constants.Version2) ? Utils.Constants.ContextBindingMsg : null,
                BindingMessage = _apiVersion.Equals(Utils.Constants.Version2) ? Utils.Constants.ContextBindingMsg : null
            };
            var response = await _mobileConnect.StartAuthentication(Request, sdksession, subscriberId, null, null, options);
            if (response.ErrorMessage != null)
            {
                return string.Format("Authentication was failed: '{0}' , with code: {1}", response.ErrorMessage, response.ErrorCode);
            }

            CachedParameters.nonce = response.Nonce;
            await responseChecker.SaveData(response.State, CachedParameters);

            return response.Url;
        }

        public async Task<MobileConnectStatus> RequestIdentity(string state = null, string accessToken = null)
        {
            var cachedInfo = responseChecker.getData(state);
            MobileConnectStatus response = null;

            try
            {
                response = await _mobileConnect.RequestIdentityAsync(Request, cachedInfo.Result.sdkSession,
                    accessToken == null ? cachedInfo.Result.accessToken : accessToken, new MobileConnectRequestOptions());
            }
            catch (Exception e)
            {
                if (cachedInfo.Result != null)
                {
                    return response;
                }
                response = MobileConnectStatus.Error(ErrorCodes.InvalidArgument, "state value is incorrect", e);
            }

            return response;
        }

        private IHttpActionResult CreateResponse(MobileConnectStatus status)
        {
            var response = Request.CreateResponse(HttpStatusCode.OK, ResponseConverter.Convert(status));
            if (status.SetCookie != null)
            {
                foreach (var cookie in status.SetCookie)
                {
                    response.Headers.Add("Set-Cookie", cookie);
                }
            }
            return new ResponseMessageResult(response);
        }

        private IHttpActionResult CreateIdentityResponse(MobileConnectStatus status, MobileConnectStatus authnStatus)
        {
            var response = Request.CreateResponse(HttpStatusCode.OK, ResponseConverter.Convert(status));
            var authnResponse = Request.CreateResponse(HttpStatusCode.OK, ResponseConverter.Convert(authnStatus));
            authnResponse.Content = new StringContent(createNewHttpResponseMessage(response, authnResponse));
            if (status.SetCookie != null)
            {
                foreach (var cookie in status.SetCookie)
                {
                    authnResponse.Headers.Add("Set-Cookie", cookie);
                }
            }
            return new ResponseMessageResult(authnResponse);
        }

        private string createNewHttpResponseMessage(HttpResponseMessage response, HttpResponseMessage authnResponse)
        {
            dynamic convertResponseToJson = JsonConvert.DeserializeObject(response.Content.ReadAsStringAsync().Result);
            dynamic convertAuthnResponseToJson = JsonConvert.DeserializeObject(authnResponse.Content.ReadAsStringAsync().Result);
            return "{" + "\"access_token\":" + "\"" + convertAuthnResponseToJson.token.access_token + "\"" + 
                ", \"token_type\":" + "\"" + convertAuthnResponseToJson.token.token_type + "\"" + 
                ", \"id_token\":" + "\"" + convertAuthnResponseToJson.token.id_token + "\"" + 
                ", \"identity\":" + convertResponseToJson.identity + "}";
        }

        private void GetParameters()
        {
            _operatorParams = readAndParseFiles.ReadFile(Utils.Constants.ConfigFilePath);
            _apiVersion = _operatorParams.apiVersion;
            _includeRequestIP = _operatorParams.includeRequestIP.Equals("True");
            _cache = new ConcurrentCache();
            _restClient = new RestClient();
            _mobileConnectConfig = new MobileConnectConfig()
            {
                ClientId = _operatorParams.clientID,
                ClientSecret = _operatorParams.clientSecret,
                DiscoveryUrl = _operatorParams.discoveryURL,
                RedirectUrl = _operatorParams.redirectURL,
                XRedirect = _operatorParams.xRedirect.Equals("True") ? "APP" : "False"
            };
            _mobileConnect = new MobileConnectWebInterface(_mobileConnectConfig, _cache, _restClient);
        }
    }
}
