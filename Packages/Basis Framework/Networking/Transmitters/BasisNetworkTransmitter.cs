using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.NetworkedPlayer;
using DarkRift;
using DarkRift.Server.Plugins.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static SerializableDarkRift;

namespace Basis.Scripts.Networking.Transmitters
{
    [DefaultExecutionOrder(15001)]
    public partial class BasisNetworkTransmitter : BasisNetworkSendBase
    {
        public bool HasEvents = false;
        public float timer = 0f;
        public float interval = 0.0333333333333333f;
        public float SmallestDistanceToAnotherPlayer;
        [SerializeField]
        public BasisAudioTransmission AudioTransmission = new BasisAudioTransmission();
        public NativeArray<float3> targetPositions;
        public NativeArray<float> distances;
        public NativeArray<bool> DistanceResults;

        public NativeArray<bool> HearingResults;
        public NativeArray<bool> AvatarResults;
        public NativeArray<float> smallestDistance;


        [SerializeField]
        public LocalAvatarSyncMessage LASM = new LocalAvatarSyncMessage();
        public float UnClampedInterval;

        public static float DefaultInterval = 0.0333333333333333f;
        public static float BaseMultiplier = 1f; // Starting multiplier.
        public static float IncreaseRate = 0.0075f; // Rate of increase per unit distance.

        public int InitalizedLength = -1;
        public CombinedDistanceAndClosestTransformJob distanceJob = new CombinedDistanceAndClosestTransformJob();
        public JobHandle distanceJobHandle;
        public int IndexLength;
        public void Compute()
        {
            if (Ready && NetworkedPlayer.Player.Avatar != null)
            {
                BasisNetworkAvatarCompressor.Compress(this, NetworkedPlayer.Player.Avatar.Animator);
            }
        }
        public float SlowestSendRate = 2.5f;
        void SendOutLatest()
        {
            timer += Time.deltaTime;

            if (timer >= interval)
            {
                ScheduleCheck();
                Compute();
                distanceJobHandle.Complete();
                HandleResults();
                SmallestDistanceToAnotherPlayer = distanceJob.smallestDistance[0];

                // Calculate next interval and clamp it
                UnClampedInterval = DefaultInterval * (BaseMultiplier + (SmallestDistanceToAnotherPlayer * IncreaseRate));
                interval = math.clamp(UnClampedInterval, 0.005f, SlowestSendRate);

                // Account for overshoot
                timer -= interval;
            }
        }
        public bool[] MicrophoneRangeIndex;
        public bool[] LastMicrophoneRangeIndex;

        public bool[] HearingIndex;
        public bool[] AvatarIndex;
        public ushort[] HearingIndexToId;
        public void HandleResults()
        {
            if (distanceJob.DistanceResults == null)
            {
                return;
            }
            if (MicrophoneRangeIndex == null)
            {
                return;
            }
            if (MicrophoneRangeIndex.Length != distanceJob.DistanceResults.Length)
            {
                return;
            }
            distanceJob.DistanceResults.CopyTo(MicrophoneRangeIndex);
            distanceJob.HearingResults.CopyTo(HearingIndex);
            distanceJob.AvatarResults.CopyTo(AvatarIndex);

            MicrophoneOutputCheck();
            Iteration();
        }
        /// <summary>
        /// how far we can hear locally
        /// </summary>
        public void Iteration()
        {
            for (int Index = 0; Index < IndexLength; Index++)
            {
                Recievers.BasisNetworkReceiver Rec = BasisNetworkManagement.ReceiverArray[Index];
                if (Rec.AudioReceiverModule.IsPlaying != HearingIndex[Index])
                {
                    if (HearingIndex[Index])
                    {
                        Rec.AudioReceiverModule.StartAudio();
                        Rec.RemotePlayer.OutOfRangeFromLocal = false;
                    }
                    else
                    {
                        Rec.AudioReceiverModule.StopAudio();
                        Rec.RemotePlayer.OutOfRangeFromLocal = true;
                    }
                }
                /*
                if (Rec.RemotePlayer.IsNotFallBack != AvatarIndex[Index])
                {
                    if (AvatarIndex[Index])
                    {
                        BasisLoadableBundle BasisLoadableBundle = BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(Rec.RemotePlayer.CACM.byteArray);

                        Rec.RemotePlayer.CreateAvatar(Rec.RemotePlayer.CACM.loadMode, BasisLoadableBundle);
                        Rec.RemotePlayer.IsNotFallBack = true;
                    }
                    else
                    {
                     //   BasisAvatarFactory.LoadLoadingAvatar(Rec.RemotePlayer, BasisAvatarFactory.LoadingAvatar.BasisLocalEncryptedBundle.LocalBundleFile);
                       // Rec.RemotePlayer.IsNotFallBack = false;
                    }
                }
                */
            }
        }
        /// <summary>
        ///lets the server know who can hear us.
        /// </summary>
        public void MicrophoneOutputCheck()
        {

            if (AreBoolArraysEqual(MicrophoneRangeIndex, LastMicrophoneRangeIndex) == false)
            {
                //Debug.Log("Arrays where not equal!");
                Array.Copy(MicrophoneRangeIndex, LastMicrophoneRangeIndex, IndexLength);
                List<ushort> TalkingPoints = new List<ushort>();
                for (int Index = 0; Index < IndexLength; Index++)
                {
                    bool User = MicrophoneRangeIndex[Index];
                    if (User)
                    {
                        TalkingPoints.Add(HearingIndexToId[Index]);
                    }
                }
                if (TalkingPoints.Count != 0)
                {
                    HasReasonToSendAudio = true;
                }
                else
                {
                    HasReasonToSendAudio = false;
                }
                //even if we are not listening to anyone we still need to tell the server that!
                VoiceReceiversMessage VRM = new VoiceReceiversMessage
                {
                    users = TalkingPoints.ToArray()
                };
                using (DarkRiftWriter writer = DarkRiftWriter.Create())
                {
                    writer.Write(VRM);
                    using (Message msg = Message.Create(BasisTags.AudioCommunication, writer))
                    {
                        BasisNetworkManagement.Instance.Client.SendMessage(msg, BasisNetworking.VoiceChannel, DeliveryMethod.ReliableOrdered);
                       // Debug.Log("sending out voice Receivers");
                    }
                }
            }
        }
        public static bool AreBoolArraysEqual(bool[] array1, bool[] array2)
        {
            // Check if both arrays are null
            if (array1 == null && array2 == null)
            {
                return true;
            }

            // Check if one of them is null
            if (array1 == null || array2 == null)
            {
                return false;
            }

            int Arraylength = array1.Length;
            // Check if lengths differ
            if (Arraylength != array2.Length)
            {
                return false;
            }

            // Compare values
            for (int Index = 0; Index < Arraylength; Index++)
            {
                if (array1[Index] != array2[Index])
                {
                    return false;
                }
            }

            return true;
        }
        public void OnDestroy()
        {
            DeInitialize();
        }
        public override void Initialize(BasisNetworkedPlayer networkedPlayer)
        {
            if (Ready == false)
            {
                NetworkedPlayer = networkedPlayer;
                AudioTransmission.OnEnable(networkedPlayer);
                OnAvatarCalibration();
                if (HasEvents == false)
                {
                    NetworkedPlayer.Player.OnAvatarSwitchedFallBack += OnAvatarCalibration;
                    NetworkedPlayer.Player.OnAvatarSwitched += OnAvatarCalibration;
                    NetworkedPlayer.Player.OnAvatarSwitched += SendOutLatestAvatar;
                    BasisLocalInputActions.AfterAvatarChanges += SendOutLatest;
                    BasisNetworkManagement.OnRemotePlayerJoined += OnRemoteJoined;
                    BasisNetworkManagement.OnRemotePlayerLeft += OnRemoteLeft;
                    ResizeOrCreateArrayData(BasisNetworkManagement.ReceiverCount);
                    HasEvents = true;
                }
                Ready = true;
            }
            else
            {
                Debug.Log("Already Ready");
            }
        }
        private void OnRemoteLeft(BasisNetworkedPlayer player1, BasisRemotePlayer player2)
        {
            ResizeOrCreateArrayData(BasisNetworkManagement.ReceiverCount);
        }
        private void OnRemoteJoined(BasisNetworkedPlayer player1, BasisRemotePlayer player2)
        {
            ResizeOrCreateArrayData(BasisNetworkManagement.ReceiverCount);
        }
        public void ScheduleCheck()
        {
            distanceJob.AvatarDistance = SMModuleDistanceBasedReductions.AvatarRange;
            distanceJob.HearingDistance = SMModuleDistanceBasedReductions.HearingRange;
            distanceJob.VoiceDistance = SMModuleDistanceBasedReductions.MicrophoneRange;
            distanceJob.referencePosition = NetworkedPlayer.MouthBone.OutgoingWorldData.position;
            smallestDistance[0] = float.MaxValue;
            for (int Index = 0; Index < BasisNetworkManagement.ReceiverCount; Index++)
            {
                targetPositions[Index] = BasisNetworkManagement.ReceiverArray[Index].NetworkedPlayer.MouthBone.OutgoingWorldData.position;
            }
            if (IndexLength != BasisNetworkManagement.ReceiverCount)
            {
                LastMicrophoneRangeIndex = new bool[BasisNetworkManagement.ReceiverCount];
                MicrophoneRangeIndex = new bool[BasisNetworkManagement.ReceiverCount];
                HearingIndex = new bool[BasisNetworkManagement.ReceiverCount];
                AvatarIndex = new bool[BasisNetworkManagement.ReceiverCount];

                IndexLength = BasisNetworkManagement.ReceiverCount;
                HearingIndexToId = BasisNetworkManagement.RemotePlayers.Keys.ToArray();
            }
            distanceJobHandle = distanceJob.Schedule(targetPositions.Length, 64);
        }
        public void ResizeOrCreateArrayData(int TotalUserCount)
        {
            if (InitalizedLength != TotalUserCount)
            {
                if (distanceJobHandle.IsCompleted == false)
                {
                    distanceJobHandle.Complete();
                }
                if (targetPositions.IsCreated)
                {
                    targetPositions.Dispose();
                }
                if (distances.IsCreated)
                {
                    distances.Dispose();
                }
                if (smallestDistance.IsCreated)
                {
                    smallestDistance.Dispose();
                }
                if (DistanceResults.IsCreated)
                {
                    DistanceResults.Dispose();
                }
                if (HearingResults.IsCreated)
                {
                    HearingResults.Dispose();
                }
                if (AvatarResults.IsCreated)
                {
                    AvatarResults.Dispose();
                }
                smallestDistance = new NativeArray<float>(1, Allocator.Persistent);
                smallestDistance[0] = float.MaxValue;
                targetPositions = new NativeArray<float3>(TotalUserCount, Allocator.Persistent);
                distances = new NativeArray<float>(TotalUserCount, Allocator.Persistent);
                DistanceResults = new NativeArray<bool>(TotalUserCount, Allocator.Persistent);

                HearingResults = new NativeArray<bool>(TotalUserCount, Allocator.Persistent);
                AvatarResults = new NativeArray<bool>(TotalUserCount, Allocator.Persistent);

                InitalizedLength = TotalUserCount;

                // Step 2: Find closest index in the next frame
                distanceJob.distances = distances;
                distanceJob.DistanceResults = DistanceResults;
                distanceJob.HearingResults = HearingResults;
                distanceJob.AvatarResults = AvatarResults;


                distanceJob.targetPositions = targetPositions;

                distanceJob.smallestDistance = smallestDistance;
            }
        }
        public override void DeInitialize()
        {
            if (Ready)
            {
                AudioTransmission.OnDisable();
            }
            if (HasEvents)
            {
                NetworkedPlayer.Player.OnAvatarSwitchedFallBack -= OnAvatarCalibration;
                NetworkedPlayer.Player.OnAvatarSwitched -= OnAvatarCalibration;
                NetworkedPlayer.Player.OnAvatarSwitched -= SendOutLatestAvatar;
                BasisLocalInputActions.AfterAvatarChanges -= SendOutLatest;
                BasisNetworkManagement.OnRemotePlayerJoined -= OnRemoteJoined;
                BasisNetworkManagement.OnRemotePlayerLeft -= OnRemoteLeft;
                if (targetPositions.IsCreated) targetPositions.Dispose();
                if (distances.IsCreated) distances.Dispose();
                if (smallestDistance.IsCreated)
                {
                    smallestDistance.Dispose();
                }
                if (DistanceResults.IsCreated)
                {
                    DistanceResults.Dispose();
                }
                if (HearingResults.IsCreated)
                {
                    HearingResults.Dispose();
                }
                if (AvatarResults.IsCreated)
                {
                    AvatarResults.Dispose();
                }
                HasEvents = false;
            }
        }
        public void SendOutLatestAvatar()
        {
            byte[] LAI = BasisBundleConversionNetwork.ConvertBasisLoadableBundleToBytes(NetworkedPlayer.Player.AvatarMetaData);
            using (DarkRiftWriter writer = DarkRiftWriter.Create())
            {
                ClientAvatarChangeMessage ClientAvatarChangeMessage = new ClientAvatarChangeMessage
                {
                    byteArray = LAI,
                    loadMode = NetworkedPlayer.Player.AvatarLoadMode,
                };
                writer.Write(ClientAvatarChangeMessage);
                using (var msg = Message.Create(BasisTags.AvatarChangeMessage, writer))
                {
                    BasisNetworkManagement.Instance.Client.SendMessage(msg, BasisNetworking.EventsChannel, DeliveryMethod.ReliableOrdered);
                }
            }
        }
        [BurstCompile]
        public struct CombinedDistanceAndClosestTransformJob : IJobParallelFor
        {
            public float VoiceDistance;
            public float HearingDistance;
            public float AvatarDistance;
            [ReadOnly]
            public float3 referencePosition;
            [ReadOnly]
            public NativeArray<float3> targetPositions;

            [WriteOnly]
            public NativeArray<float> distances;
            [WriteOnly]
            public NativeArray<bool> DistanceResults;
            [WriteOnly]
            public NativeArray<bool> HearingResults;
            [WriteOnly]
            public NativeArray<bool> AvatarResults;

            // Shared result for the smallest distance
            [NativeDisableParallelForRestriction]
            public NativeArray<float> smallestDistance;

            public void Execute(int index)
            {
                // Calculate distance
                Vector3 diff = targetPositions[index] - referencePosition;
                float sqrDistance = diff.sqrMagnitude;
                distances[index] = sqrDistance;

                // Determine boolean results
                DistanceResults[index] = sqrDistance < VoiceDistance;
                HearingResults[index] = sqrDistance < HearingDistance;
                AvatarResults[index] = sqrDistance < AvatarDistance;

                // Update the smallest distance (atomic operation to avoid race conditions)
                float currentSmallest = smallestDistance[0];
                if (sqrDistance < currentSmallest)
                {
                    smallestDistance[0] = sqrDistance;
                }
            }
        }
    }
}