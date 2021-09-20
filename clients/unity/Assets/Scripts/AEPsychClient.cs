/*
Copyright (c) Facebook, Inc. and its affiliates.
All rights reserved.

This source code is licensed under the license found in the
LICENSE file in the root directory of this source tree.
*/


using NetMQ;
using NetMQ.Sockets;
using System.Collections.Generic;
using System.Collections;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.IO;

namespace AEPsych
{
    public enum RequestType { setup, ask, tell, resume, query, parameters };
    public enum QueryType { min, max, prediction, inverse }

    // TODO make this a little more strongly typed
    public class TrialConfig : Dictionary<string, List<float>> { }

    public class Request
    {
        // this should definitely be more narrowly defined
        public object message;

        [JsonConverter(typeof(StringEnumConverter))]
        public RequestType type;

        public Request(object message, RequestType type)
        {
            this.message = message;
            this.type = type;
        }
    }

    public class VersionedRequest : Request
    {
        public string version;
        public VersionedRequest(object message, RequestType type, string version) : base(message, type)
        {
            this.version = version;
        }
    }

    public class TrialWithOutcome
    {
        public TrialConfig config;
        public int outcome;

        public TrialWithOutcome(TrialConfig config, int outcome)
        {
            this.config = config;
            this.outcome = outcome;
        }

    }

    public class QueryMessage
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public QueryType query_type;
        public List<float> x;
        public float y;
        public Dictionary<int, float> constraints;
        public bool probability_space;

        public QueryMessage(QueryType queryType, List<float> x, float y, Dictionary<int, float> constraints, bool probability_space)
        {
            this.query_type = queryType;
            this.x = x;
            this.y = y;
            this.constraints = constraints;
            this.probability_space = probability_space;
        }
    }

    public class ResumeMessage
    {
        public int strat_id;

        public ResumeMessage(int strat_id)
        {
            this.strat_id = strat_id;
        }
    }

    public class Target
    {
        public string target;
        public Target(string target)
        {
            this.target = target;
        }
    }


    public class SetupMessage
    {
        public string config_str;

        public SetupMessage(string configStr)
        {
            this.config_str = configStr;
        }
    }


    public class AEPsychClient : MonoBehaviour
    {
        RequestSocket client;
        public enum ClientStatus { Ready, QuerySent, GotResponse };
        ClientStatus status;
        string reply;
        public TrialConfig baseConfig;
        public int currentStrat;
        public string server_address = "tcp://localhost";
        public string server_port = "5555";
        public bool finished;

        public string ReadFile(string filePath)
        {
            var sr = new StreamReader(filePath);
            string fileContents = sr.ReadToEnd();
            sr.Close();
            return fileContents;
        }


        public IEnumerator StartServer(string configPath, string version = "0.01")
        {
            CleanupClient();
            string configStr = ReadFile(configPath);
            AsyncIO.ForceDotNet.Force();
            status = ClientStatus.QuerySent;
            client = new RequestSocket();
            client.Connect($"{this.server_address}:{this.server_port}");
            SetupMessage setupMessage = new SetupMessage(configStr: configStr);
            yield return StartCoroutine(this.SendRequest(JsonConvert.SerializeObject(new VersionedRequest(setupMessage, RequestType.setup, version))));
        }

        public void ConnectServer()
        {
            CleanupClient();
            AsyncIO.ForceDotNet.Force();
            status = ClientStatus.Ready;
            client = new RequestSocket();
            client.Connect($"{this.server_address}:{this.server_port}");
        }


        IEnumerator SendRequest(string query)
        {

            reply = null;
            bool gotMessage = false;
            Debug.Log("Sending " + query);
            client.SendFrame(query);
            status = ClientStatus.QuerySent;
            while (!gotMessage)
            {
                gotMessage = client.TryReceiveFrameString(out reply); // this returns true if it's successful
                yield return null;
            }
            if (gotMessage)
            {
                Debug.Log("Received " + reply);
            }
            status = ClientStatus.GotResponse;
        }

        public ClientStatus GetStatus()
        {
            return status;
        }

        public bool IsBusy()
        {
            return (status == ClientStatus.QuerySent);
        }

        public IEnumerator WaitForReady()
        {
            while (IsBusy())
            {
                yield return null;
            }
        }

        public TrialConfig GetConfig()
        {
            if (status != ClientStatus.GotResponse)
            {
                Debug.Log("Error! Called getConfig() when there is no reply available! Current status is " + status);
            }
            status = ClientStatus.Ready;
            baseConfig = JsonConvert.DeserializeObject<TrialConfig>(reply);
            if (baseConfig.ContainsKey("finished"))
            {
                finished = (1 == baseConfig["finished"][0]);
            }
            return baseConfig;
        }

        public int GetStrat() //can only call this after setup
        {
            if (status != ClientStatus.GotResponse)
            {
                Debug.Log("Error! Called getConfig() when there is no reply available! Current status is " + status);
            }
            status = ClientStatus.Ready;
            currentStrat = JsonConvert.DeserializeObject<int>(reply);
            return currentStrat;
        }

        public IEnumerator Tell(TrialConfig trialConfig, int outcome)
        {
            TrialWithOutcome message = new TrialWithOutcome(trialConfig, outcome);
            Request req = new Request(message, RequestType.tell);
            yield return StartCoroutine(this.SendRequest(JsonConvert.SerializeObject(req)));
        }
        public IEnumerator Ask()
        {
            Request req = new Request("", RequestType.ask);
            yield return StartCoroutine(this.SendRequest(JsonConvert.SerializeObject(req)));
        }

        public IEnumerator Params()
        {
            Request req = new Request("", RequestType.parameters);
            yield return StartCoroutine(this.SendRequest(JsonConvert.SerializeObject(req)));
        }

        public IEnumerator Query(QueryType queryType, List<float> x = null, float y = 0, Dictionary<int, float> constraints = null, bool probability_space = false)
        {
            if (x == null)
            {
                x = new List<float>() { };
            }
            if (constraints == null)
            {
                constraints = new Dictionary<int, float>() { };
            }

            QueryMessage message = new QueryMessage(queryType, x, y, constraints, probability_space);
            Request req = new Request(message, RequestType.query);
            string s = JsonConvert.SerializeObject(req);
            yield return StartCoroutine(this.SendRequest(s));
        }
        public QueryMessage GetQueryResponse()
        {
            if (status != ClientStatus.GotResponse)
            {
                Debug.Log("Error! Called getQuery() when there is no reply available! Current status is " + status);
            }
            status = ClientStatus.Ready;
            QueryMessage queryResponse = JsonConvert.DeserializeObject<QueryMessage>(reply);
            return queryResponse;
        }

        public IEnumerator Resume(int strat_id, string version = "0.01")
        {
            ResumeMessage message = new ResumeMessage(strat_id);
            VersionedRequest req = new VersionedRequest(message, RequestType.resume, version);
            yield return StartCoroutine(this.SendRequest(JsonConvert.SerializeObject(req)));
        }

        void CleanupClient()
        {
            if (client != null)
            {
                client.Close();
                NetMQConfig.Cleanup();
            }
        }

        void OnApplicationQuit()
        {
            CleanupClient();

        }

    }


}
