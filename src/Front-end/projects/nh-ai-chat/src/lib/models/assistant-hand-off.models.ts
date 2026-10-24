/**
 * Hand-offs defined as data and the shadow variants of an agent, as the administration reads and
 * changes them (`admin/agents/{id}/hand-offs` and `admin/agents/{id}/shadow-variants`).
 */
import { PackagePartLink } from './assistant-agent-package.models';

/** A hand-off the agent accepts in code (`AcceptHandOffs`), read only. */
export interface CodeHandOff {
  subjectType: string;
  /** The agents that may hand work off, by id. */
  sources: string[];
  description: string | null;
  /** The source gets a follow-up when the agent's run for the hand-off ends. */
  notifySource: boolean;
  /** Hand-offs one source may make about one subject per 24 hours. */
  maxPerSubjectPerDay: number;
  /** The generated tool the sources' tool selectors select. */
  toolId: string;
}

/**
 * A hand-off defined as data within the ceiling in code (`UseHandOffsAsData`). It works as one in
 * code; one that no longer fits the ceiling or the code is out of effect as a whole.
 */
export interface DataHandOff extends CodeHandOff {
  version: number;
  changedAt: string;
  changedBy: string;
  reason: string | null;
  /** `false` with `problem` when it no longer fits the ceiling or the code. */
  inEffect: boolean;
  /** Such as `outside-ceiling`, `unknown-source` or `limit-above-ceiling`. */
  problem: string | null;
  createdAt: string;
  createdBy: string;
  /**
   * The link of the hand-off (part `handOff:{subjectType}`) to the agent package it was imported from, with its locked
   * fields, which the administration shows read-only; missing when it is not linked.
   */
  package?: PackagePartLink | null;
}

/** The agents the ceiling lets hand work about one subject type to the agent. */
export interface HandOffAllowance {
  subjectType: string;
  /** The agents with a run configuration that may hand off now, by id; never the agent itself. */
  sources: string[];
  /** Every agent created in the administration with run settings may hand off, also those created later. */
  sourcesCreatedInAdministration: boolean;
}

/** What the ceiling in code lets the agent accept as data. */
export interface HandOffCeiling {
  subjectTypes: HandOffAllowance[];
  /** The highest daily limit per source and subject. */
  maxPerSubjectPerDay: number;
  /** The most hand-offs defined as data of one agent. */
  maxHandOffs: number;
  /** The most sources of one hand-off. */
  maxSources: number;
  maxDescriptionLength: number;
}

/** `GET admin/agents/{id}/hand-offs`. */
export interface AgentHandOffs {
  agentId: string;
  codeHandOffs: CodeHandOff[];
  dataHandOffs: DataHandOff[];
  /** `null` when the agent may accept no hand-offs as data. */
  ceiling: HandOffCeiling | null;
  /** Whether the agent has the run configuration a hand-off needs. */
  configured: boolean;
}

/** Body of `PUT admin/agents/{id}/hand-offs/{subjectType}`. */
export interface DataHandOffRequest {
  /** 1 to the ceiling's `maxSources` agents within the ceiling. */
  sources: string[];
  /** At most the ceiling's `maxDescriptionLength` characters. */
  description?: string | null;
  notifySource?: boolean;
  /** 1 to the ceiling's `maxPerSubjectPerDay`; 5 by default. */
  maxPerSubjectPerDay?: number | null;
  /** The hand-off's current version; `null` for a new one. */
  expectedVersion?: number | null;
  /** Optional, at most 500 characters. */
  reason?: string | null;
}

/** One shadow variant of a live agent. */
export interface ShadowVariant {
  agentId: string;
  displayName: string;
  /** `code` for `ShadowOf`, `data` for a variant created in the administration. */
  source: 'code' | 'data';
  /** A data variant receives the live agent's triggers only with run settings. */
  hasRunSettings: boolean;
  /** Whether the variant receives the live agent's triggers now. */
  inEffect: boolean;
  /** Why a data variant with run settings is out of effect, such as `outside-ceiling`. */
  problem: string | null;
}

/** `GET admin/agents/{id}/shadow-variants`. */
export interface AgentShadowVariants {
  agentId: string;
  /** Whether administrators may create variants of the agent as data (`UseShadowVariantsAsData`). */
  allowsDataVariants: boolean;
  /** The most data variants the agent may have; 0 when it may have none. */
  maxDataVariants: number;
  /** The variants in code first, then the data variants. */
  variants: ShadowVariant[];
}
