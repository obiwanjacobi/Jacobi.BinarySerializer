# Processor

An object that converts/translates data from one format to another. A processor is typically used to encode or decode data, such as converting audio or video files from one format to another, or compressing and decompressing data for storage or transmission.

- define a list of global/well-known properties (names and datatypes.
- publish it supported properties (well-know and custom).
- Processor impl. is specific to a pipeline phase.
- 


Processor Pipeline is a sequence of processors that are applied to data in a specific order. Each processor in the pipeline performs a specific transformation or operation on the data, such as encoding, decoding, compressing, or decompressing. The output of one processor serves as the input for the next processor in the pipeline.



                   +-- Field/GroupInfo => SchemaNode
                   |
  Processors => Context => SessionState
      |            |            |
      +------- Pipeline --------+

Write/Read-Process (Session)
  - Owns SessionState and SchemaCursor
  - Drives navigation (MoveNext / Enter / Exit)
  - Invokes Pipeline with the node's precompiled ProcessorPlan

CompiledSchema (static, cached, thread-safe)
  - SchemaNode tree (Field/GroupInfo resolved)
  - ProcessorPlan per node per direction/phase

Pipeline: stateless executor of a ExecutionPlan
Context: per-invocation view (Node + SessionState + Data)
Processors: stateless singletons; scratch state lives in SessionState

  CompiledSchema --> Session --> Cursor --> Node.Plan
                        |                     |
                   SessionState <-- Context <-- Pipeline --> Processors