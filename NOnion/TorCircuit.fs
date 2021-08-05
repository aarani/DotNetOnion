﻿namespace NOnion


open System.Security.Cryptography

open NOnion.Crypto.Kdf
open NOnion.Utility
open NOnion.Cells
open FSharpx.Control.Observable
open FSharp.Control.Reactive
open NOnion.Crypto
open System

type TorCircuit private (id: uint16, guard: TorGuard, kdfResult: KdfResult) as self =

    let circuitId = id
    let cryptoState = TorCryptoState.FromKdfResult kdfResult
    let guard = guard

    let window = TorWindow (1000, 100)

    let mutable streamsCount: int = 1
    (* Prevents two stream setup happening at once (to prevent race condition on writing to StreamIds list) *)
    let streamSetupLock: obj = obj ()

    //TODO: make cryptostate immutable and use mapFold
    //TODO: make sure late subscription doesn't make any problem here
    let circuitMessages =
        guard.CircuitMessages circuitId
        |> Observable.ofType
        |> Observable.map self.DecryptCell
        |> Observable.publish

    let subscription = circuitMessages.Connect ()

    member self.StreamMessages (streamId: uint16) =
        circuitMessages
        |> Observable.filter (fun (sid, _) -> sid = streamId)
        |> Observable.map (fun (_, cell) -> cell)

    member self.Id = circuitId

    member self.Send (streamId: uint16) (relayData: RelayData) =
        async {
            let plainRelayCell =
                CellPlainRelay.Create streamId relayData (Array.zeroCreate 4)

            let relayPlainBytes = plainRelayCell.ToBytes true

            cryptoState.ForwardDigest.Update
                relayPlainBytes
                0
                relayPlainBytes.Length

            let digest =
                cryptoState.ForwardDigest.GetDigestBytes () |> Array.take 4

            let plainRelayCell =
                { plainRelayCell with
                    Digest = digest
                }

            window.PackageDecrease ()

            do!
                {
                    CellEncryptedRelay.EncryptedData =
                        plainRelayCell.ToBytes false
                        |> cryptoState.ForwardCipher.Encrypt
                }
                |> guard.Send id
        }


    static member CreateFast (guard: TorGuard) =
        async {
            let rec createCircuitId (retry: int) =
                if retry >= Constants.MaxCircuitIdGenerationRetry then
                    failwith "can't create a circuit"

                let randomBytes = Array.zeroCreate<byte> 2

                RandomNumberGenerator
                    .Create()
                    .GetBytes randomBytes

                let cid =
                    IntegerSerialization.FromBigEndianByteArrayToUInt16
                        randomBytes

                if guard.RegisterCircuitId cid then
                    cid
                else
                    createCircuitId (retry + 1)

            let circuitId = createCircuitId 0

            let randomClientMaterial =
                Array.zeroCreate<byte> Constants.HashLength

            RandomNumberGenerator
                .Create()
                .GetBytes randomClientMaterial

            do!
                guard.Send
                    circuitId
                    {
                        CellCreateFast.X = randomClientMaterial
                    }

            let! createdMsg =
                guard.CircuitMessages circuitId
                |> Observable.ofType
                |> Async.AwaitObservable

            let kdfResult =
                Array.concat [ randomClientMaterial
                               createdMsg.Y ]
                |> Kdf.computeLegacyKdf

            if kdfResult.KeyHandshake <> createdMsg.DerivativeKeyData then
                failwith "Bad key handshake"

            return new TorCircuit (circuitId, guard, kdfResult)
        }

    static member CreateFastAsTask guard =
        TorCircuit.CreateFast guard |> Async.StartAsTask

    member internal self.RegisterStreamId () : uint16 =
        let safeRegister () =
            let newId = uint16 streamsCount
            streamsCount <- streamsCount + 1
            newId

        lock streamSetupLock safeRegister

    interface IDisposable with
        member self.Dispose () =
            subscription.Dispose ()
