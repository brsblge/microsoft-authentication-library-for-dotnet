﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Identity.Client.Core;
using Microsoft.Identity.Client.Internal;
using Microsoft.Identity.Client.Internal.Requests;
using Microsoft.Identity.Client.OAuth2;
using Microsoft.Identity.Client.Utils;
using Windows.Foundation.Metadata;
using Windows.Security.Authentication.Web.Core;
using Windows.Security.Credentials;

namespace Microsoft.Identity.Client.Platforms.Features.WamBroker
{
    internal class MsaPlugin : IWamPlugin
    {
        private const string MsaErrorCode = "wam_msa_error";
        private readonly IWamProxy _wamProxy;
        private readonly IWebAccountProviderFactory _webAccountProviderFactory;
        private readonly ICoreLogger _logger;

        public MsaPlugin(IWamProxy wamProxy, IWebAccountProviderFactory webAccountProviderFactory, ICoreLogger logger)
        {
            _wamProxy = wamProxy;
            _webAccountProviderFactory = webAccountProviderFactory;
            _logger = logger;
        }

        public async Task<WebTokenRequest> CreateWebTokenRequestAsync(
            WebAccountProvider provider,
            AuthenticationRequestParameters authenticationRequestParameters,
            bool isForceLoginPrompt,
            bool isInteractive,
            bool isAccountInWam)
        {
            bool setLoginHint = false;
            bool addNewAccount = false;

            string loginHint = authenticationRequestParameters.LoginHint ?? authenticationRequestParameters.Account?.Username;

            if (isInteractive && !isAccountInWam)
            {
                if (!string.IsNullOrEmpty(loginHint))
                {
                    setLoginHint = true;
                }
                else
                {
                    addNewAccount = !(await _webAccountProviderFactory.IsDefaultAccountMsaAsync().ConfigureAwait(false));
                }
            }

            var promptType = (setLoginHint || addNewAccount || (isForceLoginPrompt && isInteractive)) ?
                WebTokenRequestPromptType.ForceAuthentication :
                WebTokenRequestPromptType.Default;

            string scopes = ScopeHelper.GetMsalScopes(authenticationRequestParameters.Scope).AsSingleString();
            WebTokenRequest request = new WebTokenRequest(
                provider,
                scopes,
                authenticationRequestParameters.ClientId,
                promptType);

            if (addNewAccount || setLoginHint)
            {
                request.Properties.Add("Client_uiflow", "new_account"); // launch add account flow

                if (setLoginHint)
                {
                    request.Properties.Add("LoginHint", loginHint); // prefill username
                }
            }

            request.Properties.Add("api-version", "2.0"); // request V2 tokens over V1
            request.Properties.Add("oauth2_batch", "1"); // request tokens as OAuth style name/value pairs
            request.Properties.Add("x-client-info", "1"); // request client_info

            if (ApiInformation.IsPropertyPresent("Windows.Security.Authentication.Web.Core.WebTokenRequest", "CorrelationId"))
            {
                request.CorrelationId = authenticationRequestParameters.CorrelationId.ToString();
            }
            else
            {
                _logger.Warning("[WAM MSA Plugin] Could not add the correlation ID to the request.");
            }

            return request;
        }

        public string GetHomeAccountIdOrNull(WebAccount webAccount)
        {
            if (!webAccount.Properties.TryGetValue("SafeCustomerId", out string cid))
            {
                _logger.Warning("[WAM MSA Plugin] MSAL account cannot be created without MSA CID");
                return null;
            }

            if (!TryConvertCidToGuid(cid, out string localAccountId))
            {
                _logger.WarningPii($"[WAM MSA Plugin] Invalid CID: {cid}", $"[WAM MSA Provider] Invalid CID, length {cid.Length}");
                return null;
            }

            if (localAccountId == null)
            {
                return null;
            }

            string homeAccountId = localAccountId + "." + Constants.MsaTenantId;
            return homeAccountId;
        }

        /// <summary>
        /// Generally the MSA plugin will NOT return the accounts back to the app. This is due
        /// to privacy concerns. However, some test apps are allowed to do this, hence the code. 
        /// Normal 1st and 3rd party apps must use AcquireTokenInteractive to login first, and then MSAL will
        /// save the account for later use.
        /// </summary>
        public async Task<IEnumerable<IAccount>> GetAccountsAsync(string clientID)
        {
            var webAccounProvider = await _webAccountProviderFactory.GetAccountProviderAsync("consumers").ConfigureAwait(false);

            var webAccounts = await _wamProxy.FindAllWebAccountsAsync(webAccounProvider, clientID).ConfigureAwait(false);

            var msalAccounts = webAccounts
                .Select(webAcc => ConvertToMsalAccountOrNull(webAcc))
                .Where(a => a != null)
                .ToList();

            _logger.Info($"[WAM MSA Plugin] GetAccountsAsync converted {webAccounts.Count()} MSAL accounts");
            return msalAccounts;
        }

        private IAccount ConvertToMsalAccountOrNull(WebAccount webAccount)
        {
            const string environment = "login.windows.net"; //TODO: is MSA available in other clouds?
            string homeAccountId = GetHomeAccountIdOrNull(webAccount);

            return new Account(homeAccountId, webAccount.UserName, environment);
        }

        // There are two commonly used formats for MSA CIDs:
        //    - hex format, which is a fixed length 16 characters string.
        //    - GUID format, which is the hex value CID prefixed with '00000000-0000-0000-'
        // For example for hex CID value '540648eb0b3075bb' the corresponding GUID representation is
        // '00000000-0000-0000-5406-48eb0b3075bb'
        // This helper method converts MSA CID from the Hex format to GUID format.
        private bool TryConvertCidToGuid(string cid, out string localAccountId)
        {
            if (cid.Length != 16)
            {
                localAccountId = null;
                return false;
            }

            string lowercaseCid = cid.ToLowerInvariant();
            localAccountId = "00000000-0000-0000-" + lowercaseCid.Insert(4, "-");
            return true;
        }


        public string MapTokenRequestError(WebTokenRequestStatus status, uint errorCode, bool isInteractive)
        {
            if (status != WebTokenRequestStatus.UserInteractionRequired)
            {
                return MsaErrorCode;
            }

            // TODO: can further drill into errors by looking at HResult 
            // https://github.com/AzureAD/microsoft-authentication-library-for-cpp/blob/75de1a8aee5f83d86941de6081fa351f207d9446/source/windows/broker/MSATokenRequest.cpp#L104

            return MsalError.InteractionRequired;
        }

        public MsalTokenResponse ParseSuccesfullWamResponse(WebTokenResponse webTokenResponse)
        {
            string msaTokens = webTokenResponse.Token;
            if (string.IsNullOrEmpty(msaTokens))
            {
                throw new MsalServiceException(
                    MsaErrorCode, 
                    "Internal error - bad token format, msaTokens was unexpectedly empty");
            }

            string accessToken = null, idToken = null, clientInfo = null, tokenType = null, scopes = null, correlationId = null;
            long expiresIn = 0;

            foreach (string keyValuePairString in msaTokens.Split('&'))
            {
                string[] keyValuePair = keyValuePairString.Split('=');
                if (keyValuePair.Length != 2)
                {
                    throw new MsalClientException(
                        MsaErrorCode,
                        "Internal error - bad token response format, expected '=' separated pair");
                }

                if (keyValuePair[0] == "access_token")
                {
                    accessToken = keyValuePair[1];
                }
                else if (keyValuePair[0] == "id_token")
                {
                    idToken = keyValuePair[1];
                }
                else if (keyValuePair[0] == "token_type")
                {
                    tokenType = keyValuePair[1];
                }
                else if (keyValuePair[0] == "scope")
                {
                    scopes = keyValuePair[1];
                }
                else if (keyValuePair[0] == "client_info")
                {
                    clientInfo = keyValuePair[1];
                }
                else if (keyValuePair[0] == "expires_in")
                {
                    expiresIn = long.Parse(keyValuePair[1], CultureInfo.InvariantCulture);
                }
                else if (keyValuePair[0] == "correlation")
                {
                    correlationId = keyValuePair[1];
                }
                //else
                //{
                //    // TODO: C++ code saves the remaining properties, but I did not find a reason why                    
                //    Debug.WriteLine($"{keyValuePair[0]}={keyValuePair[1]}");
                //}
            }

            if (string.IsNullOrEmpty(tokenType) || string.Equals("bearer", tokenType, System.StringComparison.OrdinalIgnoreCase))
            {
                tokenType = "Bearer";
            }

            if (string.IsNullOrEmpty(scopes))
            {
                throw new MsalClientException(
                    MsaErrorCode,
                    "Internal error - bad token response format, no scopes");
            }

            var responseScopes = scopes.Replace("%20", " ");

            MsalTokenResponse msalTokenResponse = new MsalTokenResponse()
            {
                AccessToken = accessToken,
                IdToken = idToken,
                CorrelationId = correlationId,
                Scope = responseScopes,
                ExpiresIn = expiresIn,
                ExtendedExpiresIn = 0, // not supported on MSA
                ClientInfo = clientInfo,
                TokenType = tokenType,
                WamAccountId = webTokenResponse.WebAccount.Id,
                TokenSource = TokenSource.Broker
            };

            return msalTokenResponse;
        }      
    }
}
