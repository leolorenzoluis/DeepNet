﻿namespace LangRNN


open Basics
open ArrayNDNS
open SymTensor
open SymTensor.Compiler.Cuda

open Models
open Datasets
open Optimizers


module GRULang =

    type HyperPars = {
        NWords:                     SizeSpecT
        EmbeddingDim:               SizeSpecT
    }

    let defaultHyperPars = {
        NWords                      = SizeSpec.fix 0
        EmbeddingDim                = SizeSpec.fix 0
    }

    type Pars = {        
        WordToEmb:       ExprT    // [word, embDim]
        EmbToUpdate:     ExprT    // [recUnit, embDim]
        EmbToReset:      ExprT    // [recUnit, embDim]
        EmbToHidden:     ExprT    // [recUnit, embDim]
        StateToUpdate:   ExprT    // [recUnit, recUnit]
        StateToReset:    ExprT    // [recUnit, recUnit]
        StateToHidden:   ExprT    // [recUnit, recUnit]
        UpdateBias:      ExprT    // [recUnit]
        ResetBias:       ExprT    // [recUnit]
        HiddenBias:      ExprT    // [recUnit]
        StateToWord:     ExprT    // [word, recUnit]
        WordBias:        ExprT    // [word]
        HyperPars:       HyperPars
    }

    let internal initWeights seed (shp: int list) = 
        let r = 1.0f / sqrt (single shp.[1])       
        (System.Random seed).SeqSingle(-r, r) |> ArrayNDHost.ofSeqWithShape shp
        
    let internal initBias seed (shp: int list) =
        ArrayNDHost.zeros<single> shp

    let pars (mb: ModelBuilder<_>) hp = {
        WordToEmb     = mb.Param ("WordToEmb",     [hp.NWords; hp.EmbeddingDim],       initWeights)
        EmbToUpdate   = mb.Param ("EmbToUpdate",   [hp.EmbeddingDim; hp.EmbeddingDim], initWeights)
        EmbToReset    = mb.Param ("EmbToReset",    [hp.EmbeddingDim; hp.EmbeddingDim], initWeights)
        EmbToHidden   = mb.Param ("EmbToHidden",   [hp.EmbeddingDim; hp.EmbeddingDim], initWeights)
        StateToUpdate = mb.Param ("StateToUpdate", [hp.EmbeddingDim; hp.EmbeddingDim], initWeights)
        StateToReset  = mb.Param ("StateToReset",  [hp.EmbeddingDim; hp.EmbeddingDim], initWeights)
        StateToHidden = mb.Param ("StateToHidden", [hp.EmbeddingDim; hp.EmbeddingDim], initWeights)
        UpdateBias    = mb.Param ("UpdateBias",    [hp.EmbeddingDim],                  initBias)
        ResetBias     = mb.Param ("ResetBias",     [hp.EmbeddingDim],                  initBias)
        HiddenBias    = mb.Param ("HiddenBias",    [hp.EmbeddingDim],                  initBias)
        StateToWord   = mb.Param ("StateToWord",   [hp.EmbeddingDim; hp.NWords],       initWeights)
        WordBias      = mb.Param ("WordBias",      [hp.NWords],                        initBias)
        HyperPars     = hp
    }

    let sigmoid (z: ExprT) = 1.0f / (1.0f + exp (-z))
    let softmax (z: ExprT) = exp z / (Expr.sumKeepingAxis 1 (exp z))

    let negLogSoftmax (z: ExprT) =
        let c = z |> Expr.maxKeepingAxis 1
        c - z + log (Expr.sumKeepingAxis 1 (exp (z - c)))
        

    let pred (pars: Pars) (initialSlice: ExprT) (words: ExprT) (genFirstWord: ExprT) =
        // words            [smpl, pos]
        // input            [smpl, step]           
        // initial          [smpl, recUnit]
        // genFirstWord     [smpl]
        // state            [smpl, step, recUnit]
        // output           [smpl, step, word]

        let nBatch = words.Shape.[0]
        let nSteps = words.Shape.[1] - 1
        let embeddingDim = pars.HyperPars.EmbeddingDim

        let initial      = initialSlice |> Expr.reshape [nBatch; SizeSpec.fix 1; embeddingDim]
        let genFirstWord = genFirstWord |> Expr.reshape [nBatch; SizeSpec.fix 1]

        // build loop
        let inputSlice = Expr.var<int>    "InputSlice"  [nBatch] 
        let prevState  = Expr.var<single> "PrevState"   [nBatch; embeddingDim]

        let bcEmbUnit = Expr.arange<int> embeddingDim |> Expr.padLeft |> Expr.broadcast [nBatch; embeddingDim]
        let bcInputSlice = inputSlice |> Expr.padRight |> Expr.broadcast [nBatch; embeddingDim]
        let emb = pars.WordToEmb |> Expr.gather [Some bcInputSlice; Some bcEmbUnit]

        let update = emb .* pars.EmbToUpdate + prevState .* pars.StateToUpdate           + Expr.padLeft pars.UpdateBias |> sigmoid
        let reset  = emb .* pars.EmbToReset  + prevState .* pars.StateToReset            + Expr.padLeft pars.ResetBias  |> sigmoid
        let hidden = emb .* pars.EmbToHidden + (prevState * reset) .* pars.StateToHidden + Expr.padLeft pars.HiddenBias |> tanh
        let state  = (1.0f - update) * hidden + update * prevState
        let output = state .* pars.StateToWord + Expr.padLeft pars.WordBias
        let pred   = output |> Expr.argMaxAxis 1 
        let logWordProb = output |> negLogSoftmax

        // training loop
        let chState, chLogWordProb, chPred = "State", "LogWordProb", "Pred"
        let loopSpec = {
            Expr.Length = nSteps
            Expr.Vars = Map [Expr.extractVar inputSlice, Expr.SequenceArgSlice {ArgIdx=0; SliceDim=1}
                             Expr.extractVar prevState, 
                                    Expr.PreviousChannel {Channel=chState; Delay=SizeSpec.fix 1; InitialArg=1}]
            Expr.Channels = Map [chState,       {LoopValueT.Expr=state;  LoopValueT.SliceDim=1}
                                 chLogWordProb, {LoopValueT.Expr=logWordProb; LoopValueT.SliceDim=1}]    
        }
        let input        = words.[*, 0 .. nSteps-1]
        let states       = Expr.loop loopSpec chState  [input; initial]
        let logWordProbs = Expr.loop loopSpec chLogWordProb [input; initial]
        let finalState   = states.[*, nSteps-1, *]

        let target       = words.[*, 1 .. nSteps]
        let loss         = logWordProbs |> Expr.gather [None; None; Some target] |> Expr.mean 

        // generating loop
        let genSteps = SizeSpec.fix 200
        let genLoopSpec = {
            Expr.Length = genSteps
            Expr.Vars = Map [Expr.extractVar inputSlice, 
                                    Expr.PreviousChannel {Channel=chPred;  Delay=SizeSpec.fix 1; InitialArg=0}
                             Expr.extractVar prevState, 
                                    Expr.PreviousChannel {Channel=chState; Delay=SizeSpec.fix 1; InitialArg=1}]
            Expr.Channels = Map [chState,  {LoopValueT.Expr=state;  LoopValueT.SliceDim=1}
                                 chPred,   {LoopValueT.Expr=pred;   LoopValueT.SliceDim=1}]    
        }
        let states        = Expr.loop genLoopSpec chState  [genFirstWord; initial; initialSlice]
        let generated     = Expr.loop genLoopSpec chPred   [genFirstWord; initial; initialSlice]
        let genFinalState = states.[*, genSteps-1, *]

        (finalState, logWordProbs, loss), (genFinalState, generated)



type GRUTrain (VocSize:      int,
               EmbeddingDim: int) =

    let mb = ModelBuilder<single> ("M")

    let embeddingDim = mb.Size "embeddingDim"
    let nWords       = mb.Size "nWords"
    let nBatch       = mb.Size "nBatch"
    let nSteps       = mb.Size "nSteps"

    let words      = mb.Var<int>     "Words"      [nBatch; nSteps]
    let initial    = mb.Var<single>  "Initial"    [nBatch; embeddingDim]
    let firstWord  = mb.Var<int>     "FirstWord"  [nBatch]

    let model = GRULang.pars (mb.Module "GRULang") {
        NWords       = nWords
        EmbeddingDim = embeddingDim
    }      

    let (final, pred, loss), (genFinal, genWords) = (initial, words, firstWord) |||> GRULang.pred model

    let mi = mb.Instantiate (DevCuda, Map [nWords,       VocSize
                                           embeddingDim, EmbeddingDim])

    let generateFn = mi.Func<single, int> (genFinal, genWords) |> arg2 initial firstWord
    let processFn  = mi.Func<single>      (final)              |> arg2 initial words                       

    member this.Train (dataset: TrnValTst<WordSeq>) trainCfg =
        let smplVarEnv (stateOpt: ArrayNDT<single> option) (smpl: WordSeq) =
            let nBatch = smpl.Words.Shape.[0]
            let state =
                match stateOpt with
                | Some state -> 
                    if state.Shape.[0] > nBatch then state.[0 .. nBatch-1, *]
                    else state
                | None -> 
                    ArrayNDCuda.zeros<single> [nBatch; EmbeddingDim] :> ArrayNDT<_>
            VarEnv.ofSeq [words, smpl.Words :> IArrayNDT; initial, state :> IArrayNDT]

        //let trainable = Train.newStatefulTrainable mi [loss] final smplVarEnv GradientDescent.New GradientDescent.DefaultCfg
        let trainable = Train.newStatefulTrainable mi [loss] final smplVarEnv Adam.New Adam.DefaultCfg
        Train.train trainable dataset trainCfg |> ignore

    member this.Generate seed (startWords: WordSeq) =
        // sw [smpl, step]
        let sw = startWords.Words
        let nBatch, nStart = sw.Shape.[0], sw.Shape.[1]

        let rng = System.Random seed

//        let initial = rng.UniformArrayND (-1.0f, 1.0f) [nBatch; EmbeddingDim]
//                      |> ArrayNDCuda.toDev :> ArrayNDT<_>
        let initial = ArrayNDCuda.zeros<single> [nBatch; EmbeddingDim] :> ArrayNDT<single>
        let primed = 
            if nStart > 1 then processFn initial sw.[*, 0 .. nStart-2]
            else initial

        let final, gen = generateFn primed sw.[*, nStart-1]
        {Words=gen}




