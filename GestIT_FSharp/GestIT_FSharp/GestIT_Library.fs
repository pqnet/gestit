namespace GestIT

open System.Runtime.InteropServices

type SensorEventArgs<'T,'U> when 'T :> System.Enum and 'U :> System.EventArgs (t:'T, e:'U) =
  inherit System.EventArgs()
  member x.FeatureType = t
  member x.Event = e

type ISensor<'T,'U> when 'T :> System.Enum and 'U :> System.EventArgs =
  [<CLIEvent>]
  abstract member SensorEvents: IEvent<SensorEventArgs<'T,'U>>


type Token () =
  class
  end

[<AbstractClass>]
type GestureNet () =
  let completionEvent = new Event<Token seq>()
  member this.Completed(ts) = completionEvent.Trigger(ts)
  member this.Completion = completionEvent.Publish
  abstract member Front: GestureNet list
  abstract member AddTokens: Token seq -> unit
  abstract member RemoveTokens: Token seq -> unit


[<AbstractClass>]
type GestureExpr<'T,'U> when 'T :> System.Enum and 'U :> System.EventArgs () =
  let gestureEvent = new Event<_>()
  member this.Gestured(e) = gestureEvent.Trigger(this, e)
  [<CLIEvent>]
  member this.Gesture = gestureEvent.Publish
  abstract member ToNet: ISensor<'T,'U> -> GestureNet
  member this.ToInternalGestureNet(s) =
    let net = this.ToNet(s)
    net.Completion.Add(fun ts -> this.Gestured())
    net
  member this.ToGestureNet(s) =
    let net = this.ToInternalGestureNet(s)
    for subn in net.Front do
      subn.Completion.Add(fun _ -> net.AddTokens([new Token()]))
    net.AddTokens([new Token()])
    net

type Predicate<'U> = delegate of 'U -> bool

type GroundTerm<'T,'U> when 'T :> System.Enum and 'U :> System.EventArgs (f:'T, ?p:Predicate<'U>) =
  inherit GestureExpr<'T,'U>()
  new(f,p) = GroundTerm<_,_>(f, new Predicate<_>(p))
  member this.Feature = f
  member this.Predicate = p
  override this.ToNet(s) = new GroundTermNet<_,_>(this, s) :> GestureNet

and private GroundTermNet<'T,'U> when 'T :> System.Enum and 'U :> System.EventArgs (exp:GroundTerm<'T,'U>, sensor:ISensor<'T,'U>) as this =
  inherit GestureNet()

  let mutable tokens = new System.Collections.Generic.HashSet<Token>()
  let mutable handler:System.IDisposable = null

  let clearHandler() =
    if handler <> null then
      handler.Dispose()
      handler <- null

  let handle (event:SensorEventArgs<'T,'U>) =
    if (exp.Feature :> System.Enum).Equals(event.FeatureType) then
      let p =
        match exp.Predicate with
          | None -> true
          | Some d -> d.Invoke(event.Event)
      if p then
        let oldtokens = tokens
        tokens <- new System.Collections.Generic.HashSet<Token>()
        clearHandler()
        this.Completed(oldtokens)

  override this.Front = [this]

  override this.AddTokens(ts) =
    for t in ts do
      tokens.Add(t) |> ignore
    if handler = null then
      handler <- sensor.SensorEvents.Subscribe(handle)

  override this.RemoveTokens(ts) =
    for t in ts do
      tokens.Remove(t) |> ignore
    if tokens.Count = 0 then
      clearHandler()

[<AbstractClass>]
type private OperatorNet ([<System.ParamArray>] subnets:GestureNet[]) =
  inherit GestureNet()

  override this.AddTokens(ts) =
    for n in this.Front do
      n.AddTokens(ts)

  override this.RemoveTokens(ts) =
    for n in subnets do
      n.RemoveTokens(ts)

type Sequence<'T,'U> when 'T :> System.Enum and 'U :> System.EventArgs (l:GestureExpr<'T,'U>, r:GestureExpr<'T,'U>) =
  inherit GestureExpr<'T,'U>()
  override this.ToNet(s) =
    let lnet = l.ToInternalGestureNet(s)
    let rnet = r.ToInternalGestureNet(s)
    let net = { new OperatorNet(lnet, rnet) with
                override this.Front = lnet.Front
                } :> GestureNet
    lnet.Completion.Add(rnet.AddTokens)
    rnet.Completion.Add(net.Completed)
    net


type Parallel<'T,'U> when 'T :> System.Enum and 'U :> System.EventArgs (l:GestureExpr<'T,'U>, r:GestureExpr<'T,'U>) =
  inherit GestureExpr<'T,'U>()
  override this.ToNet(s) =
    let lnet = l.ToInternalGestureNet(s)
    let rnet = r.ToInternalGestureNet(s)
    let net = { new OperatorNet(lnet, rnet) with
                override this.Front = lnet.Front @ rnet.Front
              } :> GestureNet
    let semicompleted = new System.Collections.Generic.HashSet<Token>()
    let mycb ts =
      let mutable comp = []
      for t in ts do
        if semicompleted.Contains(t) then
          semicompleted.Remove(t) |> ignore
          comp <- t::comp
        else
          semicompleted.Add(t) |> ignore
      if comp <> [] then
        net.Completed(comp)
    lnet.Completion.Add(mycb)
    rnet.Completion.Add(mycb)
    net

type Choice<'T,'U> when 'T :> System.Enum and 'U :> System.EventArgs (l:GestureExpr<'T,'U>, r:GestureExpr<'T,'U>) =
  inherit GestureExpr<'T,'U>()
  override this.ToNet(s) =
    let lnet = l.ToInternalGestureNet(s)
    let rnet = r.ToInternalGestureNet(s)
    let net = { new OperatorNet(lnet, rnet) with
                override this.Front = lnet.Front @ rnet.Front
                } :> GestureNet
    lnet.Completion.Add(fun ts ->
                        rnet.RemoveTokens(ts)
                        net.Completed(ts))
    rnet.Completion.Add(fun ts ->
                        lnet.RemoveTokens(ts)
                        net.Completed(ts))
    net

type Iter<'T,'U> when 'T :> System.Enum and 'U :> System.EventArgs (x:GestureExpr<'T,'U>) =
  inherit GestureExpr<'T,'U>()
  override this.ToNet(s) =
    let subnet = x.ToInternalGestureNet(s)
    let net = { new OperatorNet(subnet) with
                override this.Front = subnet.Front
                } :> GestureNet
    subnet.Completion.Add(fun ts ->
                          subnet.AddTokens(ts)
                          this.Gestured()
                          )
    net
