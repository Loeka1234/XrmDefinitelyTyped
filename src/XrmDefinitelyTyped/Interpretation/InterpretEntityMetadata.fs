﻿module internal DG.XrmDefinitelyTyped.InterpretEntityMetadata

open Utility

open IntermediateRepresentation
open InterpretOptionSetMetadata
open Microsoft.Xrm.Sdk
open Microsoft.Xrm.Sdk.Metadata
open Utility

  
let toSome convertFunc (nullable: System.Nullable<'a>) =
  match nullable.HasValue with
  | true -> nullable.GetValueOrDefault() |> convertFunc
  | false -> TsType.Any

let typeConv = function   
  | AttributeTypeCode.Boolean   -> TsType.Boolean
  | AttributeTypeCode.DateTime  -> TsType.Date
    
  | AttributeTypeCode.Memo      
  | AttributeTypeCode.EntityName
  | AttributeTypeCode.String    -> TsType.String

  | AttributeTypeCode.Integer
  | AttributeTypeCode.Double  
  | AttributeTypeCode.BigInt    
  | AttributeTypeCode.Money     
  | AttributeTypeCode.Picklist  
  | AttributeTypeCode.State     
  | AttributeTypeCode.Status    -> TsType.Number
  | _                           -> TsType.Any

let interpretVirtualAttribute (a:AttributeMetadata) (options:OptionSet option) =
  match a with
  | :? MultiSelectPicklistAttributeMetadata -> Some (TsType.Custom options.Value.displayName, SpecialType.MultiSelectOptionSet)
  | _ -> None


let interpretNormalAttribute aType (a:AttributeMetadata) (options:OptionSet option)  =
  match aType with
  | AttributeTypeCode.Money     -> TsType.Number, SpecialType.Money
    
  | AttributeTypeCode.Picklist
  | AttributeTypeCode.State
  | AttributeTypeCode.Status    -> TsType.Custom options.Value.displayName, SpecialType.OptionSet

  | AttributeTypeCode.Lookup    
  | AttributeTypeCode.PartyList  
  | AttributeTypeCode.Customer  
  | AttributeTypeCode.Owner     -> TsType.String, SpecialType.EntityReference
        
  | AttributeTypeCode.Uniqueidentifier 
                                -> TsType.String, SpecialType.Guid

  | AttributeTypeCode.Decimal   -> toSome typeConv a.AttributeType, SpecialType.Decimal
  | _                           -> toSome typeConv a.AttributeType, SpecialType.Default

let getLabelOption (label:Label) =
    match label.UserLocalizedLabel <> null with
    | true -> Some label.UserLocalizedLabel.Label
    | _ -> None

let interpretAttribute nameMap entityNames labelMapping deprecatedPrefix (a: AttributeMetadata) = 
  let aType = a.AttributeType.GetValueOrDefault()
  if a.AttributeOf <> null ||
      (aType = AttributeTypeCode.Virtual && a.AttributeTypeName <> AttributeTypeDisplayName.MultiSelectPicklistType)||
      a.LogicalName.StartsWith("yomi") then None, None
  else

  let options =
    match a with
    | :? EnumAttributeMetadata as eam -> interpretOptionSet entityNames eam.OptionSet labelMapping
    | _ -> None

  let targetEntitySets =
    match a with
    | :? LookupAttributeMetadata as lam -> 
      lam.Targets
      |> Array.choose 
        (fun k -> 
          match Map.tryFind k nameMap with
          | None -> None
          | Some tes -> Some (k, snd tes)
        )
      |> Some
    | _ -> None

  let vTypeOption = 
    match aType with
    | AttributeTypeCode.Virtual -> interpretVirtualAttribute a options
    | _ -> Some (interpretNormalAttribute aType a options)
    
  match vTypeOption with
  | None -> None, None
  | Some (vType, sType) ->

    let displayName = getLabelOption a.DisplayName

    let isDeprecated = 
      match displayName, deprecatedPrefix with
      | Some x, Some prefix -> x.StartsWith(prefix)
      | _ -> false

    options, Some {
      XrmAttribute.schemaName = a.SchemaName
      logicalName = a.LogicalName
      varType = vType
      specialType = sType
      targetEntitySets = targetEntitySets
      readable = a.IsValidForRead.GetValueOrDefault(false)
      createable = a.IsValidForCreate.GetValueOrDefault(false)
      updateable = a.IsValidForUpdate.GetValueOrDefault(false)
      isDeprecated = isDeprecated
    }

let sanitizeNavigationProptertyName string =
    if string = null then "navigationPropertyNameNotDefined"
    else string

let interpretRelationship schemaNames nameMap referencing (rel: OneToManyRelationshipMetadata) =
  let rLogical =
    if referencing then rel.ReferencedEntity
    else rel.ReferencingEntity
    
  Map.tryFind rLogical nameMap
  ?|> fun (rSchema, rSetName) ->
    let setNames =
      match rSetName = "owners" with
      | false -> [|rSchema,rSetName|]
      | true -> [|"Team","teams";"SystemUser","systemusers"|]
    
    let name =
      match rel.ReferencedEntity = rel.ReferencingEntity with
      | false -> rel.SchemaName
      | true  ->
        match referencing with
        | true  -> sprintf "Referencing%s" rel.SchemaName
        | false -> sprintf "Referenced%s" rel.SchemaName

    setNames
    |> Array.map (fun (schema,setName) ->
      let xRel = 
        { XrmRelationship.schemaName = name
          attributeName = 
            if referencing then rel.ReferencingAttribute 
            else rel.ReferencedAttribute
          navProp = 
            if referencing then rel.ReferencingEntityNavigationPropertyName
            else rel.ReferencedEntityNavigationPropertyName
            |> sanitizeNavigationProptertyName
          referencing = referencing
          relatedSetName = setName
          relatedSchemaName = schema 
        }

      rSchema, xRel)


let interpretM2MRelationship schemaNames nameMap logicalName (rel: ManyToManyRelationshipMetadata) =
  let rLogical =
    match logicalName = rel.Entity2LogicalName with
    | true  -> rel.Entity1LogicalName
    | false -> rel.Entity2LogicalName
    
  Map.tryFind rLogical nameMap
  ?|> fun (rSchema, rSetName) ->
      
    let xRel = 
      { XrmRelationship.schemaName = rel.SchemaName 
        attributeName = rel.SchemaName
        navProp = 
          if logicalName = rel.Entity2LogicalName then rel.Entity1NavigationPropertyName
          else rel.Entity2NavigationPropertyName
          |> sanitizeNavigationProptertyName
        referencing = false
        relatedSetName = rSetName
        relatedSchemaName = rSchema 
      }
    
    rSchema, xRel


let interpretEntity schemaNames nameMap labelMapping deprecatedPrefix (metadata:EntityMetadata) =
  if isNull metadata.Attributes then failwith "No attributes found!"

  let optionSets, attributes = 
    metadata.Attributes 
    |> Array.map (interpretAttribute nameMap schemaNames labelMapping deprecatedPrefix)
    |> Array.unzip

  let attributes = 
    attributes 
    |> Array.choose id 
    |> Array.toList
   
  let optionSets = 
    optionSets 
    |> Seq.choose id 
    |> Seq.distinctBy (fun x -> x.displayName) 
    |> Seq.toList
    

  let handleOneToMany referencing = function
    | null  -> Array.empty
    | x     -> 
      x 
      |> Array.choose (interpretRelationship schemaNames nameMap referencing)
      |> Array.concat
    
  let handleManyToMany logicalName = function
    | null  -> Array.empty
    | x     -> x |> Array.choose (interpretM2MRelationship schemaNames nameMap logicalName)


  let relatedEntities, relationships = 
    [ metadata.OneToManyRelationships  |> handleOneToMany false 
      metadata.ManyToOneRelationships  |> handleOneToMany true 
      metadata.ManyToManyRelationships |> handleManyToMany metadata.LogicalName 
    ] |> Array.concat
      |> List.ofArray
      |> List.unzip

  let relatedEntities = 
    relatedEntities 
    |> Set.ofList 
    |> Set.remove metadata.SchemaName 
    |> Set.toList

  { XrmEntity.typecode = metadata.ObjectTypeCode.GetValueOrDefault()
    schemaName = metadata.SchemaName
    logicalName = metadata.LogicalName
    entitySetName = metadata.EntitySetName |> Utility.stringToOption
    idAttribute = metadata.PrimaryIdAttribute
    attributes = attributes
    optionSets = optionSets
    relatedEntities = relatedEntities
    allRelationships = relationships
    availableRelationships = relationships |> List.filter (fun r -> schemaNames.Contains r.relatedSchemaName)
  }