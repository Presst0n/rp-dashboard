using Newtonsoft.Json;
using ResPublica.UILibrary.Models.Responses;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ResPublica.UILibrary.Api
{
    public class BattleNetApiHelper : IBattleNetApiHelper
    {
        private string _clientID;
        private string _clientSecret;
        const string authorizationEndpoint = "https://eu.battle.net/oauth/authorize";
        const string tokenEndpoint = "https://eu.battle.net/oauth/token";
        const string userInfoEndpoint = "https://eu.battle.net/oauth/userinfo";
        const string tokenValidationEndpoint = "https://eu.battle.net/oauth/check_token";

        private string _code;
        private string _redirectURI;

        public BattleNetApiHelper()
        {
            _clientID = ConfigurationManager.AppSettings.Get("BlizzardApiId");
            _clientSecret = ConfigurationManager.AppSettings.Get("BlizzardApiPassword");
        }

        public async Task<bool> AuthorizeAsync()
        {
            string state = RandomDataBase64url(32);
            string code_verifier = RandomDataBase64url(32);
            string code_challenge = Base64urlencodeNoPadding(Sha256(code_verifier));
            const string code_challenge_method = "S256";

            // Creates a redirect URI using an available port on the loopback address.
            _redirectURI = string.Format("http://{0}:{1}/", IPAddress.Loopback, GetRandomUnusedPort());
            //output("redirect URI: " + redirectURI);

            // Creates an HttpListener to listen for requests on that redirect URI.`
            var http = new HttpListener();
            http.Prefixes.Add(_redirectURI);
            //output("Listening..");
            http.Start();
            var redURI = Uri.EscapeDataString(_redirectURI);
            string authorizationRequest = CreateAuthorizationRequest(state, code_challenge, code_challenge_method, redURI);

            // Opens request in the browser.
            Process.Start(new ProcessStartInfo()
            {
                FileName = $"{authorizationRequest}",
                UseShellExecute = true
            });

            // Waits for the OAuth authorization response.
            var context = await http.GetContextAsync();

            // Brings this app back to the foreground.
            //this.Activate();

            // Sends an HTTP response to the browser.
            var response = context.Response;
            string responseString = string.Format("<html><head><meta http-equiv='refresh' content='10;url=https://blizzard.com'></head><body>Please return to the app.</body></html>");
            var buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            var responseOutput = response.OutputStream;

            Task responseTask = responseOutput.WriteAsync(buffer, 0, buffer.Length).ContinueWith((task) =>
            {
                responseOutput.Close();
                http.Stop();
                Console.WriteLine("HTTP server stopped.");
            });

            // Checks for errors.
            if (context.Request.QueryString.Get("error") != null)
            {
                //output(String.Format("OAuth authorization error: {0}.", context.Request.QueryString.Get("error")));
                return false;
            }
            if (context.Request.QueryString.Get("code") == null
                || context.Request.QueryString.Get("state") == null)
            {
                //output("Malformed authorization response. " + context.Request.QueryString);
                return false;
            }

            // extracts the code
            _code = context.Request.QueryString.Get("code");
            var incoming_state = context.Request.QueryString.Get("state");

            // Compares the receieved state to the expected value, to ensure that
            // this app made the request which resulted in authorization.
            if (incoming_state != state)
            {
                //output(String.Format("Received request with invalid state ({0})", incoming_state));
                return false;
            }
            //output("Authorization code: " + code);

            // Calls TokenEndpoint in order to get authorization token.
            //string token = await GetTokenAsync(code, redURI);
            //if (string.IsNullOrEmpty(token))
            //    return false;

            return true;
        }

        private string CreateAuthorizationRequest(string state, string code_challenge, string code_challenge_method, string redURI)
        {
            // Creates the OAuth 2.0 authorization request.
            return string.Format("{0}?response_type=code&client_id={2}&state={3}&redirect_uri={1}&scope=wow.profile",
                authorizationEndpoint,
                redURI,
                _clientID,
                state,
                code_challenge,
                code_challenge_method);
        }


        /// <summary>
        /// Returns URI-safe data with a given input length.
        /// </summary>
        /// <param name="length">Input length (nb. output will be longer)</param>
        /// <returns></returns>
        private static string RandomDataBase64url(uint length)
        {
            RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
            byte[] bytes = new byte[length];
            rng.GetBytes(bytes);
            return Base64urlencodeNoPadding(bytes);
        }

        /// <summary>
        /// Returns the SHA256 hash of the input string.
        /// </summary>
        /// <param name="inputStirng"></param>
        /// <returns></returns>
        private static byte[] Sha256(string inputStirng)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(inputStirng);
            SHA256Managed sha256 = new SHA256Managed();
            return sha256.ComputeHash(bytes);
        }

        /// <summary>
        /// Base64url no-padding encodes the given input buffer.
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        private static string Base64urlencodeNoPadding(byte[] buffer)
        {
            string base64 = Convert.ToBase64String(buffer);

            // Converts base64 to base64url.
            base64 = base64.Replace("+", "-");
            base64 = base64.Replace("/", "_");
            // Strips padding.
            base64 = base64.Replace("=", "");

            return base64;
        }

        private static int GetRandomUnusedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public async Task<string> GetAccessTokenAsync()
        {
            var client = new RestClient(tokenEndpoint);
            var request = new RestRequest(Method.POST);
            request.AddHeader("cache-control", "no-cache");
            request.AddHeader("content-type", "application/x-www-form-urlencoded");
            request.AddParameter("application/x-www-form-urlencoded", $"grant_type=authorization_code&code={_code}&client_id={_clientID}&client_secret={_clientSecret}&redirect_uri={_redirectURI}",
                    ParameterType.RequestBody);
            IRestResponse response = await client.ExecuteAsync(request);

            if (!response.IsSuccessful || response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            return JsonConvert.DeserializeObject<BnetAuthResponse>(response.Content)?.Access_Token;


            // At this point you should have received token from battle.net Oauth 2.0. Now you can open your view of choice and start calling Blizzard API.
        }

        public async Task<bool> ValidateTokenAsync(string token)
        {
            var client = new RestClient(tokenValidationEndpoint);
            var request = new RestRequest(Method.POST);
            request.AddHeader("cache-control", "no-cache");
            request.AddHeader("content-type", "application/x-www-form-urlencoded");
            request.AddParameter("application/x-www-form-urlencoded", $"token={token}",
                    ParameterType.RequestBody);
            IRestResponse response = await client.ExecuteAsync(request);

            if (!response.IsSuccessful)
                return false;


            return true;
        }

        public async Task<string> GetBattleTagByTokenAsync(string token)
        {
            if (string.IsNullOrEmpty(token))
                return null;

            var client = new RestClient(userInfoEndpoint);
            var request = new RestRequest(Method.GET);
            request.AddHeader("cache-control", "no-cache");
            request.AddHeader("content-type", "application/x-www-form-urlencoded");
            request.AddHeader("Authorization", $"Bearer {token}");
            IRestResponse response = await client.ExecuteAsync(request);

            return JsonConvert.DeserializeObject<UserInfoResponse>(response?.Content)?.BattleTag;
        }
    }
}
