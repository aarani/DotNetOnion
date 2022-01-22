﻿namespace NOnion.Services

open System
open System.Net
open System.Security.Cryptography
open System.Text
open System.Text.Json

open Org.BouncyCastle.Crypto.Agreement
open Org.BouncyCastle.Crypto.Digests
open Org.BouncyCastle.Crypto.Parameters
open Org.BouncyCastle.Crypto.Generators
open Org.BouncyCastle.Security

open NOnion
open NOnion.Cells.Relay
open NOnion.Crypto
open NOnion.Utility
open NOnion.Directory
open NOnion.Network

type TorServiceClient =
    private
        {
            RendezvousGuard: TorGuard
            RendezvousCircuit: TorCircuit
            Stream: TorStream
        }

    member self.GetStream() =
        self.Stream

    static member ConnectAsync
        (directory: TorDirectory)
        (connectionDetail: IntroductionPointPublicInfo)
        =
        TorServiceClient.Connect directory connectionDetail |> Async.StartAsTask

    static member Connect
        (directory: TorDirectory)
        (connectionDetail: IntroductionPointPublicInfo)
        =
        async {
            let authKey, encKey, nodeDetail, masterPubKey =

                Ed25519PublicKeyParameters(
                    connectionDetail.AuthKey |> Convert.FromBase64String,
                    0
                ),
                X25519PublicKeyParameters(
                    connectionDetail.EncryptionKey |> Convert.FromBase64String,
                    0
                ),
                CircuitNodeDetail.Create(
                    IPEndPoint(
                        IPAddress.Parse(connectionDetail.Address),
                        connectionDetail.Port
                    ),
                    connectionDetail.OnionKey |> Convert.FromBase64String,
                    connectionDetail.Fingerprint |> Convert.FromBase64String
                ),
                connectionDetail.MasterPublicKey |> Convert.FromBase64String

            let randomGeneratedCookie =
                Array.zeroCreate Constants.RendezvousCookieLength

            RandomNumberGenerator
                .Create()
                .GetNonZeroBytes randomGeneratedCookie

            let! endpoint, guardnode = directory.GetRouter RouterType.Guard
            let! _, rendNode = directory.GetRouter RouterType.Normal

            let! rendGuard = TorGuard.NewClient endpoint
            let rendCircuit = TorCircuit rendGuard

            do! rendCircuit.Create guardnode |> Async.Ignore
            do! rendCircuit.Extend rendNode |> Async.Ignore
            do! rendCircuit.RegisterAsRendezvousPoint randomGeneratedCookie

            let privateKey, publicKey =
                let kpGen = X25519KeyPairGenerator()
                let random = SecureRandom()
                kpGen.Init(X25519KeyGenerationParameters random)
                let keyPair = kpGen.GenerateKeyPair()

                keyPair.Private :?> X25519PrivateKeyParameters,
                keyPair.Public :?> X25519PublicKeyParameters

            match rendNode with
            | Create(address, onionKey, identityKey) ->
                let introduceInnerData =
                    {
                        RelayIntroduceInnerData.OnionKey = onionKey
                        RendezvousCookie = randomGeneratedCookie
                        Extensions = List.empty
                        RendezvousLinkSpecifiers =
                            [
                                LinkSpecifier.CreateFromEndPoint address
                                {
                                    LinkSpecifier.Type =
                                        LinkSpecifierType.LegacyIdentity
                                    Data = identityKey
                                }
                            ]
                    }

                let! networkStatus = directory.GetLiveNetworkStatus()
                let periodInfo = networkStatus.GetTimePeriod()

                let data, mac =
                    HiddenServicesCipher.EncryptIntroductionData
                        (introduceInnerData.ToBytes())
                        privateKey
                        publicKey
                        authKey
                        encKey
                        periodInfo
                        masterPubKey

                let introduce1Packet =
                    {
                        RelayIntroduce.AuthKey =
                            RelayIntroAuthKey.ED25519SHA3256(
                                authKey.GetEncoded()
                            )
                        Extensions = List.empty
                        ClientPublicKey = publicKey.GetEncoded()
                        Mac = mac
                        EncryptedData = data
                    }

                let introCircuit = TorCircuit rendGuard

                do! introCircuit.Create guardnode |> Async.Ignore
                do! introCircuit.Extend nodeDetail |> Async.Ignore

                let rendJoin =
                    rendCircuit.WaitingForRendezvousJoin
                        privateKey
                        publicKey
                        authKey
                        encKey

                let introduceJob =
                    async {
                        let! ack = introCircuit.Introduce introduce1Packet

                        if ack.Status <> RelayIntroduceStatus.Success then
                            return
                                failwith(
                                    sprintf
                                        "Unsuccessful introduction: %A"
                                        ack.Status
                                )
                    }

                do! Async.Parallel [ introduceJob; rendJoin ] |> Async.Ignore

                let serviceStream = TorStream rendCircuit
                do! serviceStream.ConnectToService() |> Async.Ignore

                return
                    {
                        RendezvousGuard = rendGuard
                        RendezvousCircuit = rendCircuit
                        Stream = serviceStream
                    }
            | _ -> return failwith "wat?"
        }

    interface IDisposable with
        member self.Dispose() =
            (self.RendezvousGuard :> IDisposable).Dispose()
