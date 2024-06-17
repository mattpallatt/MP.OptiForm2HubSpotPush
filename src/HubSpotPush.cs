using Castle.Core.Internal;
using EPiServer;
using EPiServer.Core;
using EPiServer.Forms.Core.Data;
using EPiServer.Forms.Core.PostSubmissionActor;
using EPiServer.Forms.Helpers.Internal;
using EPiServer.ServiceLocation;
using EPiServer.Web.Routing;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;


namespace MP.HubSpotPush
{

    public class HubspotSubmissionActor : PostSubmissionActorBase
    {
        private readonly Injected<IFormDataRepository> _formDataRepository;
        private readonly Injected<IOptions<HubSpotPushOptions>> _options;

        private string HSBearerToken = "";
        private string FormGUID = "";
        private string PortalId = "";
        private string ClientIP = "";

        private string HSFormsEndPoint = "https://api.hsforms.com/submissions/v3/integration/submit";
        private string HSAPIEndPoint = "https://api.hubapi.com/forms/v2/forms";
        private string HSCookieName = "hubspotutk";

        public override object Run(object input)
        {
            var options = _options.Service.Value;

            if (options.BearerToken != null) { HSBearerToken = options.BearerToken; } else { return null; }
            if (options.HSCookieName != null) { HSCookieName = options.HSCookieName; }

            var submission = SubmissionData;

            if (submission == null)
            {
                return string.Empty;
            }

            var repository = ServiceLocator.Current.GetInstance<IContentRepository>();
            var urlResolver = ServiceLocator.Current.GetInstance<UrlResolver>();

            var formContainerBlock = FormIdentity.GetFormBlock();

            PropertyData webhookActor = formContainerBlock.Property["CallWebhookAfterSubmissionActor"];
            var webhook = webhookActor.Value.ToJson();

            if (webhook != "[]")
            {

                JArray jsonArray = JArray.Parse(webhook);
                string webhookString = jsonArray[0]["baseUrl"].ToString();

                var PageID = SubmissionData.Data[EPiServer.Forms.Constants.SYSTEMCOLUMN_HostedPage]; // this is the page ID
                var PageData = repository.Get<PageData>(ContentReference.Parse((string)PageID)); // so this is the page data
                string PageURL = urlResolver.GetUrl(PageData.ContentLink).ToString(); // and this is the page URL


                if (webhookString.StartsWith("hs://") && !PageURL.IsNullOrEmpty())
                {

                    FormGUID = webhookString.Replace("hs://", "");

                    var submittedData = _formDataRepository.Service.TransformSubmissionDataWithFriendlyName(SubmissionData.Data, SubmissionFriendlyNameInfos, true).ToList();

                    try
                    {
                        string strHostName = Dns.GetHostName();
                        IPHostEntry ipHostInfo = Dns.Resolve(Dns.GetHostName());
                        IPAddress ipAddress = ipHostInfo.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

                        ClientIP = ipAddress?.ToString();
                    }
                    catch (Exception ex)
                    {
                        ClientIP = "1.0.0.0"; 
                    }

                    string cookieValue = HttpRequestContext.HttpContext.Request.Cookies[HSCookieName].ToString(); 

                    var formData = submittedData.ToDictionary(pair => pair.Key, pair => pair.Value);

                    if (FormGUID != null && HSBearerToken != null)
                    {
                        var response = GetFormSchemaFromHubSpot(FormGUID).Result;

                        dynamic hubspotSchema = JsonConvert.DeserializeObject(response);

                        PortalId = hubspotSchema.portalId;

                        var formSubmission = CreateFormSubmissionJson(formData, hubspotSchema, PageData.Name, PageURL, cookieValue, ClientIP);
                        var submissionEndpoint = $"{HSFormsEndPoint}/{PortalId}/{FormGUID}";
                        var result = SubmitFormDataToHubSpot(formSubmission, submissionEndpoint).Result;

                        return result;
                    }

                }
                else { return string.Empty; }
            }

            return string.Empty;
        }

        private async Task<string> GetFormSchemaFromHubSpot(string formGUID)
        {
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", HSBearerToken);

                string requestUrl = $"{HSAPIEndPoint}/{formGUID}";

                try
                {
                    HttpResponseMessage response = await httpClient.GetAsync(requestUrl);

                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        return json;
                    }
                    else
                    {

                    }
                }
                catch (Exception ex)
                {

                }
            }
            return null;
        }

        public string CreateFormSubmissionJson(Dictionary<string, object> formSubmission, dynamic hubForm, string pagename, string pageUrl, string cookieValue, string ipAddress)
        {
            var contextObject = new Dictionary<string, object>
            {
                { "pageUri", pageUrl },
                { "pageName", pagename }
            };

            if (!string.IsNullOrEmpty(cookieValue) && cookieValue.Length == 32)
            {
                contextObject["hutk"] = cookieValue;
            }

            if (!string.IsNullOrEmpty(ipAddress))
            {
                contextObject["ipAddress"] = ipAddress;
            }

            var submissionObject = new
            {
                submittedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString(),
                fields = new List<object>(),
                context = contextObject
            };


            foreach (var group in hubForm.formFieldGroups)
            {
                foreach (var field in group.fields)
                {
                    string fieldName = field.name.ToString();
                    if (formSubmission.ContainsKey(fieldName))
                    {
                        submissionObject.fields.Add(new
                        {
                            objectTypeId = field.objectTypeId.ToString(),
                            name = fieldName,
                            value = formSubmission[fieldName]
                        });
                    }
                }
            }

            return JsonConvert.SerializeObject(submissionObject, Formatting.Indented);
        }

        private async Task<string> SubmitFormDataToHubSpot(string formSubmission, string endpoint)
        {
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", HSBearerToken);
                var jsonContent = new StringContent(formSubmission, Encoding.UTF8, "application/json");

                try
                {
                    HttpResponseMessage response = await httpClient.PostAsync(endpoint, jsonContent);

                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        return json;
                    }
                    else
                    {

                    }
                }
                catch (Exception ex)
                {

                }

                return null;
            }
        }

    }

}
