using Xunit;

// DTMs reais não são seguros com várias instâncias simultâneas no mesmo processo (ver doc 08 §3), e os testes
// de porta virtual disputam o mesmo par com0com: a suíte roda sequencialmente.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
