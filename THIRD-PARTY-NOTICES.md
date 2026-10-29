# Third-party notices

The project currently references these NuGet packages. Package versions and license notices were checked against their NuGet package pages on 2026-10-29. These dependencies are not relicensed under this repository's PolyForm Noncommercial license.

## Microsoft.Web.WebView2 1.0.4314.35

Official NuGet package: <https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4314.35>

Package notice (Microsoft's permissive license):

> Copyright (C) Microsoft Corporation. All rights reserved.
>
> Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
>
> * Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
> * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
> * The name of Microsoft Corporation, or the names of its contributors may not be used to endorse or promote products derived from this software without specific prior written permission.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## HtmlAgilityPack 1.13.1

Official NuGet package: <https://www.nuget.org/packages/HtmlAgilityPack/1.13.1> · Upstream license: <https://github.com/zzzprojects/html-agility-pack/blob/master/LICENSE>

The package is MIT licensed. Its upstream license text is:

> The MIT License (MIT)
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Other assets

No book content, sample EPUBs, fonts, dictionary data, or other third-party assets are included. Only use materials whose rights and license terms have been verified before adding them. The WebView2 Evergreen Runtime is a separate Windows component; see Microsoft's [distribution guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).

The CI workflow references GitHub's `actions/checkout`, `actions/setup-dotnet`, and `actions/upload-artifact` (all pinned to major version 4). These action repositories are MIT licensed; see their upstream notices: <https://github.com/actions/checkout/blob/main/LICENSE>, <https://github.com/actions/setup-dotnet/blob/main/LICENSE>, and <https://github.com/actions/upload-artifact/blob/main/LICENSE>.
