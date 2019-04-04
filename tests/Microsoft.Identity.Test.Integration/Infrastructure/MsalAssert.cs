﻿//------------------------------------------------------------------------------
//
// Copyright (c) Microsoft Corporation.
// All rights reserved.
//
// This code is licensed under the MIT License.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
//------------------------------------------------------------------------------

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Identity.Test.LabInfrastructure;
using Microsoft.Identity.Client;
using System.Threading.Tasks;
using System.Linq;

namespace Microsoft.Identity.Test.Integration.Infrastructure
{
    public static class MsalAssert
    {
        public static async Task<IAccount> AssertSingleAccountAsync(
            LabResponse labResponse,
            IPublicClientApplication pca,
            AuthenticationResult result)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.AccessToken));
            var account = (await pca.GetAccountsAsync().ConfigureAwait(false)).Single();
            Assert.AreEqual(labResponse.User.HomeUPN, account.Username);

            return account;
        }

        public static void AssertAuthResult(AuthenticationResult result, LabUser user)
        {
            Assert.IsNotNull(result);
            Assert.IsNotNull(result.AccessToken);
            Assert.AreEqual(user.Upn, result.Account.Username);
        }
    }
}