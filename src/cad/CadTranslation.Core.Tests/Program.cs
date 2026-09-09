using CadTranslation.Contracts;
using CadTranslation.Core;

var tests = new (string Name, Action Run)[]
{
    ("equipment_codes_are_not_embedded_english_translations", () => {
        foreach (string source in new[] {
            "{\\Fisocp,hztxt|c134;MDPX150模块化破碎站（主视图）}",
            "HCQ2000磨粉机", "ABCD-123设备", "150TPH石灰石破碎筛分模块",
            "{\\FEnglishFont|c134;中文设备说明}" })
        {
            var selection = BilingualFixedLabelPolicy.Select(new[] {
                new BilingualFixedLabelSample("label", source,
                    "MDPX150 Modular Crushing Plant (Front View)", "AcDbMText", "model",
                    new Rect2(0, 0, 100, 10), 10) });
            AssertEx.Equal(0, selection.MixedObjectEnglishTextById.Count);
            AssertEx.Equal(0, selection.SuppressChineseIds.Count);
        }
    }),
    ("equipment_title_with_real_english_keeps_complete_english", () => {
        var selection = BilingualFixedLabelPolicy.Select(new[] {
            new BilingualFixedLabelSample("label", "MDPX150 破碎站 Crushing Plant",
                "MDPX150 Crushing Plant", "AcDbMText", "model", new Rect2(0, 0, 100, 10), 10) });
        AssertEx.Equal("MDPX150 Crushing Plant", selection.MixedObjectEnglishTextById["label"]);
    }),
    ("bilingual_occupancy_projects_paper_space_into_inserted_block", () => {
        var instances = new[] {
            new BlockInstancePath("*Paper_Space", "*Paper_Space", Transform2.Identity),
            new BlockInstancePath("TITLE", "*Paper_Space/TITLE[10]", Transform2.Translation(300, 10)) };
        Rect2 source = new(1100, 60, 1120, 70);
        Rect2[] projected = InstanceOccupancyProjection.Project(source, "*Paper_Space", "TITLE", instances);
        AssertEx.Equal(1, projected.Length);
        AssertEx.Equal(new Rect2(800, 50, 820, 60), projected[0]);
    }),
    ("bilingual_table_groups_exclude_frames_and_separate_tables", () => {
        var cells = new[] {
            new LayoutRegion("a", LayoutRegionKind.TableCell, new Rect2(0, 0, 10, 5)),
            new LayoutRegion("b", LayoutRegionKind.TableCell, new Rect2(0, 5, 10, 10)),
            new LayoutRegion("isolated", LayoutRegionKind.TableCell, new Rect2(20, 0, 30, 5)),
            new LayoutRegion("frame", LayoutRegionKind.ClosedFrame, new Rect2(-10, -10, 40, 40)) };
        var groups = BilingualTableLayout.Groups(cells);
        AssertEx.Equal(1, groups.Count);
        AssertEx.Equal(2, groups[0].Length);
        var rows = groups[0].OrderByDescending(r => r.Top).ToArray();
        var slots = BilingualTableLayout.SideSlots(new Rect2(0, 0, 10, 10), rows, 12, .5, true);
        AssertEx.Equal(slots[0].Left, slots[1].Left);
        AssertEx.Equal(rows[0].Center.Y, slots[0].Center.Y);
        AssertEx.True(slots[0].Bottom > slots[1].Top);
        AssertEx.True(slots.All(s => s.Right < 0));
        AssertEx.True(BilingualTableLayout.SideSlots(new Rect2(0, 0, 10, 10), rows, 12, .5, false).All(s => s.Left > 10));
    }),
    ("sha256_is_lower_hex", Tests.Sha256IsLowerHex),
    ("atomic_write_replaces_complete_file", Tests.AtomicWriteReplacesCompleteFile),
    ("mtext_fields_and_codes_are_protected", Tests.MTextFieldsAndCodesAreProtected),
    ("mtext_group_braces_are_protected_and_round_trip_exactly", Tests.MTextGroupBracesAreProtectedAndRoundTripExactly),
    ("output_restoration_localizes_protected_chinese_units_and_font_names", Tests.OutputRestorationLocalizesProtectedChineseUnitsAndFontNames),
    ("output_restoration_compacts_whitespace_before_chinese_units", Tests.OutputRestorationCompactsWhitespaceBeforeChineseUnits),
    ("translation_rejects_changed_numbers", Tests.TranslationRejectsChangedNumbers),
    ("invariant_tokenizer_does_not_treat_translated_words_as_units", Tests.InvariantTokenizerDoesNotTreatTranslatedWordsAsUnits),
    ("invariant_tokenizer_ignores_mtext_paragraph_codes", Tests.InvariantTokenizerIgnoresMTextParagraphCodes),
    ("translation_rejects_manifest_that_cannot_round_trip_raw_text", Tests.TranslationRejectsManifestThatCannotRoundTripRawText),
    ("dimension_placeholders_and_codes_are_protected", Tests.DimensionPlaceholdersAndCodesAreProtected),
    ("approved_translation_restores_protected_tokens_exactly", Tests.ApprovedTranslationRestoresProtectedTokensExactly),
    ("manifest_target_type_requires_exact_rxclass_match", Tests.ManifestTargetTypeRequiresExactRxClassMatch),
    ("candidate_content_requires_exact_restored_value_and_identity", Tests.CandidateContentRequiresExactRestoredValueAndIdentity),
    ("candidate_content_rejects_missing_duplicate_and_stale_records", Tests.CandidateContentRejectsMissingDuplicateAndStaleRecords),
    ("candidate_content_returns_manifest_validation_errors_without_throwing", Tests.CandidateContentReturnsManifestValidationErrorsWithoutThrowing),
    ("candidate_content_rejects_injected_protected_control_and_field_tokens", Tests.CandidateContentRejectsInjectedProtectedControlAndFieldTokens),
    ("candidate_content_compares_numeric_invariants_to_manifest_source", Tests.CandidateContentComparesNumericInvariantsToManifestSource),
    ("text_structure_signature_rejects_property_changes_but_not_text_content", Tests.TextStructureSignatureRejectsPropertyChangesButNotTextContent),
    ("import_write_decision_skips_identity_and_selects_only_changed_records", Tests.ImportWriteDecisionSkipsIdentityAndSelectsOnlyChangedRecords),
    ("import_layout_decision_skips_protected_only_output_normalization", Tests.ImportLayoutDecisionSkipsProtectedOnlyOutputNormalization),
    ("verify_command_is_wired_to_the_drawing_verifier", Tests.VerifyCommandIsWiredToDrawingVerifier),
    ("compose_command_is_wired_to_the_logical_flow_prototype", Tests.ComposeCommandIsWiredToLogicalFlowPrototype),
    ("verify_structure_errors_still_read_candidate_content", Tests.VerifyStructureErrorsStillReadCandidateContent),
    ("verify_preflight_failure_has_safe_atomic_artifact_route", Tests.VerifyPreflightFailureHasSafeAtomicArtifactRoute),
    ("job_config_directory_resolves_to_its_parent_job_root", Tests.JobConfigDirectoryResolvesToItsParentJobRoot)
    ,("output_mode_policy_normalizes_legacy_english", Tests.OutputModePolicyNormalizesLegacyEnglish)
    ,("output_mode_policy_rejects_unknown_mode", Tests.OutputModePolicyRejectsUnknownMode)
    ,("layout_fit_wraps_before_compressing_or_shrinking", Tests.LayoutFitWrapsBeforeCompressingOrShrinking)
    ,("layout_fit_respects_width_and_height_floors", Tests.LayoutFitRespectsWidthAndHeightFloors)
    ,("fixed_labels_never_use_emergency_ten_percent_height", Tests.FixedLabelsNeverUseEmergencyTenPercentHeight)
    ,("bilingual_additive_labels_can_use_emergency_height_steps", Tests.BilingualAdditiveLabelsCanUseEmergencyHeightSteps)
    ,("bilingual_dense_cell_fallback_stays_inside_cell_bottom", Tests.BilingualDenseCellFallbackStaysInsideCellBottom)
    ,("bilingual_width_candidates_prefer_unwrapped_width_when_space_allows", Tests.BilingualWidthCandidatesPreferUnwrappedWidthWhenSpaceAllows)
    ,("bilingual_width_candidates_keep_wrapped_options_when_single_line_does_not_fit", Tests.BilingualWidthCandidatesKeepWrappedOptionsWhenSingleLineDoesNotFit)
    ,("narrative_classifier_converts_only_long_left_aligned_dbtext", Tests.NarrativeClassifierConvertsOnlyLongLeftAlignedDbText)
    ,("layout_text_metrics_estimate_cjk_and_latin_widths", Tests.LayoutTextMetricsEstimateCjkAndLatinWidths)
    ,("layout_collision_flags_only_new_severe_overlap", Tests.LayoutCollisionFlagsOnlyNewSevereOverlap)
    ,("grid_lines_create_a_table_cell", Tests.GridLinesCreateATableCell)
    ,("merged_cell_ignores_ticks_from_neighboring_rows", Tests.MergedCellIgnoresTicksFromNeighboringRows)
    ,("region_assignment_selects_the_smallest_trustworthy_region", Tests.RegionAssignmentSelectsTheSmallestTrustworthyRegion)
    ,("region_assignment_leaves_outside_text_unassigned", Tests.RegionAssignmentLeavesOutsideTextUnassigned)
    ,("vertical_separator_creates_independent_note_columns", Tests.VerticalSeparatorCreatesIndependentNoteColumns)
    ,("transform_maps_local_bounds_into_world_space", Tests.TransformMapsLocalBoundsIntoWorldSpace)
    ,("layout_risk_subtracts_source_overlap_before_scoring", Tests.LayoutRiskSubtractsSourceOverlapBeforeScoring)
    ,("cross_region_and_anchor_drift_are_always_high_risk", Tests.CrossRegionAndAnchorDriftAreAlwaysHighRisk)
    ,("new_overlap_uses_soft_five_and_fifteen_percent_thresholds", Tests.NewOverlapUsesSoftFiveAndFifteenPercentThresholds)
    ,("standalone_punctuation_contact_does_not_block_layout", Tests.StandalonePunctuationContactDoesNotBlockLayout)
    ,("unchanged_text_pair_does_not_create_translation_overlap_risk", Tests.UnchangedTextPairDoesNotCreateTranslationOverlapRisk)
    ,("cross_region_audit_ignores_unchanged_preexisting_overflow", Tests.CrossRegionAuditIgnoresUnchangedPreexistingOverflow)
    ,("nested_block_definition_is_expanded_for_every_world_instance", Tests.NestedBlockDefinitionIsExpandedForEveryWorldInstance)
    ,("table_cell_region_routes_long_text_to_table_layout", Tests.TableCellRegionRoutesLongTextToTableLayout)
    ,("text_alignment_maps_to_matching_mtext_attachment", Tests.TextAlignmentMapsToMatchingMTextAttachment)
    ,("mtext_conversion_preserves_visual_anchor_within_tolerance", Tests.MTextConversionPreservesVisualAnchorWithinTolerance)
    ,("unassigned_text_is_preserved_without_reflow", Tests.UnassignedTextIsPreservedWithoutReflow)
    ,("stable_text_start_bands_create_note_columns_without_separator_lines", Tests.StableTextStartBandsCreateNoteColumnsWithoutSeparatorLines)
    ,("low_and_medium_layout_risks_do_not_fail_hard_gate", Tests.LowAndMediumLayoutRisksDoNotFailHardGate)
    ,("content_reopen_and_structure_failures_remain_hard_failures", Tests.ContentReopenAndStructureFailuresRemainHardFailures)
    ,("correction_policy_allows_bounded_reopened_feedback_passes", Tests.CorrectionPolicyAllowsBoundedReopenedFeedbackPasses)
    ,("instance_coverage_reports_every_missing_block_path", Tests.InstanceCoverageReportsEveryMissingBlockPath)
    ,("text_bounds_estimator_recovers_missing_autocad_extents", Tests.TextBoundsEstimatorRecoversMissingAutocadExtents)
    ,("layout_audit_uses_baseline_only_for_unmodified_unmeasurable_text", Tests.LayoutAuditUsesBaselineOnlyForUnmodifiedUnmeasurableText)
    ,("dense_long_text_bands_create_narrative_columns", Tests.DenseLongTextBandsCreateNarrativeColumns)
    ,("side_by_side_texts_receive_mutually_exclusive_allowed_boxes", Tests.SideBySideTextsReceiveMutuallyExclusiveAllowedBoxes)
    ,("narrow_label_receives_space_between_neighbor_centers", Tests.NarrowLabelReceivesSpaceBetweenNeighborCenters)
    ,("short_title_block_labels_do_not_create_narrative_columns", Tests.ShortTitleBlockLabelsDoNotCreateNarrativeColumns)
    ,("widely_scattered_long_text_does_not_create_narrative_columns", Tests.WidelyScatteredLongTextDoesNotCreateNarrativeColumns)
    ,("candidate_content_accepts_only_explicit_dbtext_to_mtext_layout_identity", Tests.CandidateContentAcceptsOnlyExplicitDbTextToMTextLayoutIdentity)
    ,("generated_width_wrapper_is_removed_without_stripping_source_formatting", Tests.GeneratedWidthWrapperIsRemovedWithoutStrippingSourceFormatting)
    ,("high_layout_risk_blocks_candidate_publication", Tests.HighLayoutRiskBlocksCandidatePublication)
    ,("overlap_audit_includes_unassigned_and_cross_region_pairs", Tests.OverlapAuditIncludesUnassignedAndCrossRegionPairs)
    ,("borderless_note_blocks_receive_separate_occupancy_regions", Tests.BorderlessNoteBlocksReceiveSeparateOccupancyRegions)
    ,("short_note_heading_is_kept_with_numbered_rows", Tests.ShortNoteHeadingIsKeptWithNumberedRows)
    ,("layout_coverage_reports_every_unhandled_changed_record", Tests.LayoutCoverageReportsEveryUnhandledChangedRecord)
    ,("narrative_occupancy_returns_explicit_group_membership", Tests.NarrativeOccupancyReturnsExplicitGroupMembership)
    ,("narrative_reflow_includes_unchanged_members_of_changed_group", Tests.NarrativeReflowIncludesUnchangedMembersOfChangedGroup)
    ,("narrative_envelope_expands_into_source_whitespace", Tests.NarrativeEnvelopeExpandsIntoSourceWhitespace)
    ,("neighboring_narrative_envelopes_remain_mutually_exclusive", Tests.NeighboringNarrativeEnvelopesRemainMutuallyExclusive)
    ,("narrative_envelope_stops_before_unrelated_source_text", Tests.NarrativeEnvelopeStopsBeforeUnrelatedSourceText)
    ,("transitive_start_bands_do_not_merge_distant_note_blocks", Tests.TransitiveStartBandsDoNotMergeDistantNoteBlocks)
    ,("dense_indented_note_rows_merge_into_macro_columns", Tests.DenseIndentedNoteRowsMergeIntoMacroColumns)
    ,("fragment_bridges_do_not_merge_neighboring_macro_columns", Tests.FragmentBridgesDoNotMergeNeighboringMacroColumns)
    ,("distant_note_panels_are_partitioned_before_macro_column_merge", Tests.DistantNotePanelsArePartitionedBeforeMacroColumnMerge)
    ,("overlong_source_bounds_do_not_defeat_anchor_partition", Tests.OverlongSourceBoundsDoNotDefeatAnchorPartition)
    ,("dense_macro_columns_include_short_rows_between_note_seeds", Tests.DenseMacroColumnsIncludeShortRowsBetweenNoteSeeds)
    ,("dense_macro_columns_exclude_distant_long_schedule_labels", Tests.DenseMacroColumnsExcludeDistantLongScheduleLabels)
    ,("dense_macro_columns_exclude_distant_drawing_labels", Tests.DenseMacroColumnsExcludeDistantDrawingLabels)
    ,("dense_macro_columns_include_short_vertical_continuations", Tests.DenseMacroColumnsIncludeShortVerticalContinuations)
    ,("narrative_row_planner_keeps_same_height_fragments_on_one_row", Tests.NarrativeRowPlannerKeepsSameHeightFragmentsOnOneRow)
    ,("narrative_horizontal_partitioner_splits_distant_sheet_panels", Tests.NarrativeHorizontalPartitionerSplitsDistantSheetPanels)
    ,("narrative_right_boundary_ignores_a_vertically_disjoint_region", Tests.NarrativeRightBoundaryIgnoresVerticallyDisjointRegion)
    ,("narrative_row_clusters_preserve_large_source_gaps", Tests.NarrativeRowClustersPreserveLargeSourceGaps)
    ,("narrative_row_boxes_fallback_when_source_centers_are_outside", Tests.NarrativeRowBoxesFallbackWhenSourceCentersAreOutside)
    ,("narrative_inline_fragments_wrap_as_one_visual_row", Tests.NarrativeInlineFragmentsWrapAsOneVisualRow)
    ,("source_neighbor_slots_are_mutually_exclusive_on_same_row", Tests.SourceNeighborSlotsAreMutuallyExclusiveOnSameRow)
    ,("layout_v2_assigns_every_changed_record_once", Tests.LayoutV2AssignsEveryChangedRecordOnce)
    ,("layout_v2_clamps_narrative_before_right_keepout", Tests.LayoutV2ClampsNarrativeBeforeRightKeepout)
    ,("layout_v2_clamps_fixed_label_before_upper_keepout", Tests.LayoutV2ClampsFixedLabelBeforeUpperKeepout)
    ,("layout_v2_partitions_fixed_label_peers", Tests.LayoutV2PartitionsFixedLabelPeers)
    ,("layout_v2_keeps_table_text_inside_parent_cell", Tests.LayoutV2KeepsTableTextInsideParentCell)
    ,("layout_v2_runs_one_fit_and_one_audit", Tests.LayoutV2RunsOneFitAndOneAudit)
    ,("layout_v2_narrative_fragments_share_one_panel", Tests.LayoutV2NarrativeFragmentsShareOnePanel)
    ,("layout_v2_classifies_large_unframed_mtext_as_narrative", Tests.LayoutV2ClassifiesLargeUnframedMTextAsNarrative)
    ,("layout_v2_classifies_long_notes_inside_sheet_frame_as_narrative", Tests.LayoutV2ClassifiesLongNotesInsideSheetFrameAsNarrative)
    ,("topology_capture_excludes_erased_entities", Tests.TopologyCaptureExcludesErasedEntities)
    ,("topology_capture_excludes_regenerated_dimension_text", Tests.TopologyCaptureExcludesRegeneratedDimensionText)
    ,("fixed_label_slot_uses_free_space_until_neighbor_midpoint", Tests.FixedLabelSlotUsesFreeSpaceUntilNeighborMidpoint)
    ,("isolated_fixed_label_slot_stays_close_to_source_visual_width", Tests.IsolatedFixedLabelSlotStaysCloseToSourceVisualWidth)
    ,("isolated_fixed_label_slot_allows_source_height_english_label", Tests.IsolatedFixedLabelSlotAllowsSourceHeightEnglishLabel)
    ,("fixed_label_moves_inside_before_reducing_text_height", Tests.FixedLabelMovesInsideBeforeReducingTextHeight)
    ,("fixed_label_padding_never_excludes_its_source_text", Tests.FixedLabelPaddingNeverExcludesItsSourceText)
    ,("large_inner_frame_block_is_a_text_container", Tests.LargeInnerFrameBlockIsATextContainer)
    ,("sheet_frame_uses_inner_print_boundary", Tests.SheetFrameUsesInnerPrintBoundary)
    ,("long_centered_fixed_label_wraps_before_emergency_compression", Tests.LongCenteredFixedLabelWrapsBeforeEmergencyCompression)
    ,("mtext_format_codes_do_not_turn_short_drawing_labels_into_narrative_notes", Tests.MTextFormatCodesDoNotTurnShortDrawingLabelsIntoNarrativeNotes)
    ,("narrative_regions_are_classified_from_source_text_not_longer_translation", Tests.NarrativeRegionsAreClassifiedFromSourceTextNotLongerTranslation)
    ,("cjk_narrative_rows_use_information_weight_not_latin_character_count", Tests.CjkNarrativeRowsUseInformationWeightNotLatinCharacterCount)
    ,("high_geometry_contact_is_soft_when_text_overlap_gate_is_clear", Tests.HighGeometryContactIsSoftWhenTextOverlapGateIsClear)
    ,("table_cell_anchor_move_inside_cell_is_medium_review", Tests.TableCellAnchorMoveInsideCellIsMediumReview)
    ,("existing_mtext_wrap_width_is_never_expanded_to_the_whole_free_slot", Tests.ExistingMTextWrapWidthIsNeverExpandedToTheWholeFreeSlot)
    ,("narrative_group_includes_center_aligned_continuation_inside_its_source_span", Tests.NarrativeGroupIncludesCenterAlignedContinuationInsideItsSourceSpan)
    ,("narrative_group_includes_short_numbered_centered_heading", Tests.NarrativeGroupIncludesShortNumberedCenteredHeading)
    ,("narrative_group_includes_short_centered_technical_fragment_inside_span", Tests.NarrativeGroupIncludesShortCenteredTechnicalFragmentInsideSpan)
    ,("emergency_text_scaling_can_fit_dense_english_without_crossing_ten_percent", Tests.EmergencyTextScalingCanFitDenseEnglishWithoutCrossingTenPercent)
    ,("aggregate_note_fit_is_retried_when_any_text_is_outside_the_region", Tests.AggregateNoteFitIsRetriedWhenAnyTextIsOutsideTheRegion)
    ,("mtext_actual_box_is_measured_from_attachment_and_rotation", Tests.MTextActualBoxIsMeasuredFromAttachmentAndRotation)
    ,("global_collision_correction_selects_only_adjusted_sides_of_high_text_pairs", Tests.GlobalCollisionCorrectionSelectsOnlyAdjustedSidesOfHighTextPairs)
    ,("stacked_table_texts_receive_vertically_exclusive_boxes", Tests.StackedTableTextsReceiveVerticallyExclusiveBoxes)
    ,("reopened_candidate_bounds_drive_conservative_correction_scale", Tests.ReopenedCandidateBoundsDriveConservativeCorrectionScale)
    ,("anchored_available_size_respects_the_nearest_slot_boundary", Tests.AnchoredAvailableSizeRespectsTheNearestSlotBoundary)
    ,("reopened_feedback_restarts_from_source_height_with_readable_floor", Tests.ReopenedFeedbackRestartsFromSourceHeightWithReadableFloor)
    ,("inline_measurement_padding_reserves_rendered_ink_safety", Tests.InlineMeasurementPaddingReservesRenderedInkSafety)
    ,("correction_region_keeps_the_source_vertical_envelope", Tests.CorrectionRegionKeepsTheSourceVerticalEnvelope)
    ,("block_reference_structure_ignores_text_dependent_geometric_extents", Tests.BlockReferenceStructureIgnoresTextDependentGeometricExtents)
    ,("title_block_attributes_can_scale_in_place_to_stay_inside_their_cell", Tests.TitleBlockAttributesCanScaleInPlaceToStayInsideTheirCell)
    ,("logical_text_composer_rebuilds_fragmented_visual_rows", Tests.LogicalTextComposerRebuildsFragmentedVisualRows)
    ,("logical_text_composer_marks_large_vertical_gaps", Tests.LogicalTextComposerMarksLargeVerticalGaps)
    ,("logical_composition_rejects_a_segment_that_still_overflows_at_the_readable_floor", Tests.LogicalCompositionRejectsOverflowAtReadableFloor)
    ,("bilingual_narrative_prefers_existing_english_mtext_over_duplicate_translation", Tests.BilingualNarrativePrefersExistingEnglishMText)
    ,("bilingual_fixed_label_suppresses_equivalent_separate_chinese_label", Tests.BilingualFixedLabelSuppressesEquivalentSeparateChineseLabel)
    ,("bilingual_fixed_label_matches_inflected_title_block_labels", Tests.BilingualFixedLabelMatchesInflectedTitleBlockLabels)
    ,("bilingual_fixed_label_matches_title_block_synonyms", Tests.BilingualFixedLabelMatchesTitleBlockSynonyms)
    ,("bilingual_fixed_label_keeps_existing_english_inside_mixed_object", Tests.BilingualFixedLabelKeepsExistingEnglishInsideMixedObject)
    ,("bilingual_fixed_label_matches_stacked_company_name", Tests.BilingualFixedLabelMatchesStackedCompanyName)
    ,("bilingual_fixed_label_learns_repeated_two_column_pairs", Tests.BilingualFixedLabelLearnsRepeatedTwoColumnPairs)
    ,("technical_code_inside_chinese_label_is_not_treated_as_bilingual", Tests.TechnicalCodeInsideChineseLabelIsNotTreatedAsBilingual)
    ,("mtext_font_name_and_tpd_unit_are_not_treated_as_existing_english", Tests.MTextFontNameAndTpdUnitAreNotTreatedAsExistingEnglish)
    ,("chinese_only_narrative_still_requires_composition", Tests.ChineseOnlyNarrativeStillRequiresComposition)
    ,("fragmented_narrative_detector_finds_split_prose", Tests.FragmentedNarrativeDetectorFindsSplitProse)
    ,("narrative_detector_finds_continuous_single_object_prose_column", Tests.NarrativeDetectorFindsContinuousSingleObjectProseColumn)
    ,("fragmented_narrative_detector_rejects_title_block_grid", Tests.FragmentedNarrativeDetectorRejectsTitleBlockGrid)
    ,("fragmented_narrative_region_expands_to_short_rows_in_the_same_column", Tests.FragmentedNarrativeRegionExpandsToShortRowsInTheSameColumn)
    ,("logical_text_segments_stop_at_source_obstacle_gaps", Tests.LogicalTextSegmentsStopAtSourceObstacleGaps)
    ,("fragmented_narrative_table_row_filter_preserves_prose_and_skips_distributed_cells", Tests.FragmentedNarrativeTableRowFilterPreservesProseAndSkipsDistributedCells)
    ,("narrative_candidate_policy_reuses_only_unclaimed_note_columns", Tests.NarrativeCandidatePolicyReusesOnlyUnclaimedNoteColumns)
    ,("narrative_panel_planner_shares_vertical_envelope_across_three_columns", Tests.NarrativePanelPlannerSharesVerticalEnvelopeAcrossThreeColumns)
    ,("narrative_panel_planner_uses_second_pass_to_add_a_fourth_column", Tests.NarrativePanelPlannerUsesSecondPassToAddAFourthColumn)
    ,("authoritative_note_selector_splits_neighboring_prose_columns", Tests.AuthoritativeNoteSelectorSplitsNeighboringProseColumns)
    ,("authoritative_note_selector_excludes_short_diagram_labels", Tests.AuthoritativeNoteSelectorExcludesShortDiagramLabels)
    ,("authoritative_note_selector_excludes_formatted_short_table_labels", Tests.AuthoritativeNoteSelectorExcludesFormattedShortTableLabels)
    ,("authoritative_note_selector_preserves_distant_sheet_panels", Tests.AuthoritativeNoteSelectorPreservesDistantSheetPanels)
};

return TestRunner.Run(tests);

internal static class Tests
{
    public static void BilingualAdditiveLabelsCanUseEmergencyHeightSteps()
    {
        double[] steps = BilingualPlacementPolicy.HeightScales.ToArray();
        AssertEx.Equal(.45, steps[0]);
        AssertEx.True(steps.SequenceEqual(steps.OrderByDescending(value => value)));
        AssertEx.Contains(LayoutFitPolicy.EmergencyMinimumHeightScale, steps);
    }

    public static void BilingualDenseCellFallbackStaysInsideCellBottom()
    {
        Rect2 result = BilingualPlacementPolicy.PlaceAtCellBottom(new Rect2(10, 20, 17, 25), 2, .4, .1);
        AssertEx.True(new Rect2(10, 20, 17, 25).Contains(result, 1e-9));
        AssertEx.Equal(20.1, result.Bottom);
        AssertEx.Equal(20.5, result.Top);
    }

    public static void BilingualWidthCandidatesPreferUnwrappedWidthWhenSpaceAllows()
    {
        double[] widths = BilingualPlacementPolicy.CandidateWidths(20, 4, 2, 10).ToArray();
        AssertEx.Equal(10.0, widths[0]);
    }

    public static void BilingualWidthCandidatesKeepWrappedOptionsWhenSingleLineDoesNotFit()
    {
        double[] widths = BilingualPlacementPolicy.CandidateWidths(12, 4, 2, 18).ToArray();
        AssertEx.False(widths.Contains(18.0));
        AssertEx.True(widths.All(width => width <= 12));
        AssertEx.True(widths.Length > 0);
    }

    public static void AuthoritativeNoteSelectorSplitsNeighboringProseColumns()
    {
        var samples = new List<FragmentedNarrativeSample>();
        double[] starts = [0, 120, 250];
        for (int column = 0; column < starts.Length; column++)
        {
            samples.AddRange(Enumerable.Range(0, 5).Select(row =>
                new FragmentedNarrativeSample(
                    $"note-{column}-{row}",
                    new Rect2(starts[column], 90 - row * 10, starts[column] + 90, 98 - row * 10),
                    $"{row + 1}. 这是第{column + 1}栏中需要独立排版的专业设计说明文字。",
                    true)));
        }
        samples.Add(new FragmentedNarrativeSample(
            "bridge-label",
            new Rect2(80, 44, 95, 52),
            "图一",
            true));
        samples.Add(new FragmentedNarrativeSample(
            "table-label",
            new Rect2(270, 34, 295, 42),
            "过梁表",
            true));

        NarrativeOccupancyGroup[] groups = AuthoritativeNarrativeSelector.SelectGroups(samples, 10);

        AssertEx.Equal(3, groups.Length);
        AssertEx.True(groups.All(group => group.MemberIds.Count == 5));
        AssertEx.True(groups.All(group => !group.MemberIds.Contains("bridge-label")));
        AssertEx.True(groups.All(group => !group.MemberIds.Contains("table-label")));
    }

    public static void AuthoritativeNoteSelectorExcludesShortDiagramLabels()
    {
        FragmentedNarrativeSample[] samples =
        [
            new("note-1", new Rect2(0, 90, 90, 98), "11.1 顶层墙体构造应符合设计及现行规范要求。", true),
            new("note-2", new Rect2(0, 80, 90, 88), "11.2 墙体拉结钢筋应沿墙体通长连续设置。", true),
            new("note-3", new Rect2(0, 70, 90, 78), "11.3 构造柱与砌体交接处应采取可靠连接措施。", true),
            new("note-4", new Rect2(0, 60, 90, 68), "11.4 后砌隔墙顶部应按施工要求填塞密实。", true),
            new("diagram-1", new Rect2(10, 72, 25, 79), "图一", true),
            new("diagram-2", new Rect2(30, 62, 55, 69), "用于单向板", true),
            new("diagram-3", new Rect2(50, 52, 70, 59), "过梁表", true)
        ];

        NarrativeOccupancyGroup[] groups = AuthoritativeNarrativeSelector.SelectGroups(samples, 8);

        AssertEx.Equal(1, groups.Length);
        AssertEx.SequenceEqual(
            ["note-1", "note-2", "note-3", "note-4"],
            groups[0].MemberIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    // Break caught: MText formatting codes make short equipment-table labels
    // look like long prose and collapse a 13-row, two-column legend into one note.
    public static void AuthoritativeNoteSelectorExcludesFormattedShortTableLabels()
    {
        var samples = new List<FragmentedNarrativeSample>();
        string[] equipmentNames =
        [
            "压缩空气（客户自备）", "电气控制系统", "包装除尘器", "小包机", "吨包机",
            "吨包/小包仓", "库底硫化散装系统", "罐车仓", "罗茨风机输送系统",
            "螺旋输送机", "主风机", "脉冲收尘器", "设备名称"
        ];
        for (int row = 0; row < equipmentNames.Length; row++)
        {
            samples.Add(new FragmentedNarrativeSample(
                $"item-{row}",
                new Rect2(0, 120 - row * 9, 8, 127 - row * 9),
                $@"\T1.001;{25 - row}",
                true));
            samples.Add(new FragmentedNarrativeSample(
                $"name-{row}",
                new Rect2(12, 120 - row * 9, 72, 127 - row * 9),
                $@"\T1.001;{equipmentNames[row]}",
                true));
        }

        NarrativeOccupancyGroup[] groups =
            AuthoritativeNarrativeSelector.SelectPanelGroups(samples, 8);

        AssertEx.Equal(0, groups.Length);
    }

    public static void AuthoritativeNoteSelectorPreservesDistantSheetPanels()
    {
        var samples = new List<FragmentedNarrativeSample>();
        double[] starts = [0, 120, 250, 1000, 1120, 1250];
        for (int column = 0; column < starts.Length; column++)
        {
            samples.AddRange(Enumerable.Range(0, 4).Select(row =>
                new FragmentedNarrativeSample(
                    $"panel-note-{column}-{row}",
                    new Rect2(starts[column], 90 - row * 10, starts[column] + 90, 98 - row * 10),
                    $"{row + 1}. 第{column + 1}栏独立设计说明应保持在原图纸分区内。",
                    true)));
        }

        NarrativeOccupancyGroup[] groups =
            AuthoritativeNarrativeSelector.SelectPanelGroups(samples, 10);

        AssertEx.Equal(6, groups.Length);
        AssertEx.True(groups.All(group => group.Region.Bounds.Width <= 100));
    }


    public static void LogicalTextComposerRebuildsFragmentedVisualRows()
    {
        LogicalComposedRow[] rows = LogicalTextComposer.ComposeRows(
            [
                new LogicalTextFragment("close", new Rect2(70, 98, 72, 102), "）。"),
                new LogicalTextFragment("design", new Rect2(30, 98, 45, 102), "Design"),
                new LogicalTextFragment("construction-a", new Rect2(0, 98, 12, 102), "Construction"),
                new LogicalTextFragment("year-label", new Rect2(60, 98, 68, 102), "Year"),
                new LogicalTextFragment("construction-b", new Rect2(14, 98, 26, 102), "Construction"),
                new LogicalTextFragment("open", new Rect2(47, 98, 49, 102), "（"),
                new LogicalTextFragment("year", new Rect2(51, 98, 58, 102), "2009")
            ],
            medianTextHeight: 4);

        AssertEx.Equal(1, rows.Length);
        AssertEx.Equal("Construction Design (2009).", rows[0].Text);
        AssertEx.SequenceEqual(
            ["construction-a", "construction-b", "design", "open", "year", "year-label", "close"],
            rows[0].MemberIds);
        AssertEx.False(rows[0].ParagraphGapBefore);
    }

    public static void LogicalTextComposerMarksLargeVerticalGaps()
    {
        LogicalComposedRow[] rows = LogicalTextComposer.ComposeRows(
            [
                new LogicalTextFragment("heading", new Rect2(0, 98, 20, 102), "Design Basis"),
                new LogicalTextFragment("body", new Rect2(0, 78, 60, 82), "First requirement.")
            ],
            medianTextHeight: 4);

        AssertEx.Equal(2, rows.Length);
        AssertEx.False(rows[0].ParagraphGapBefore);
        AssertEx.True(rows[1].ParagraphGapBefore);
    }

    public static void LogicalCompositionRejectsOverflowAtReadableFloor()
    {
        AssertEx.False(LogicalCompositionFitPolicy.ShouldReplace(
            actualHeight: 40764,
            availableHeight: 18315));
        AssertEx.True(LogicalCompositionFitPolicy.ShouldReplace(
            actualHeight: 17900,
            availableHeight: 18315));
    }

    public static void BilingualNarrativePrefersExistingEnglishMText()
    {
        BilingualNarrativeSelection selection = BilingualNarrativePolicy.Select(
            [
                new BilingualNarrativeSample("cn-1", "基础施工应符合设计及规范要求，开挖后及时验槽。", "AcDbText"),
                new BilingualNarrativeSample("cn-2", "基础混凝土强度等级为C30，保护层厚度为50mm。", "AcDbText"),
                new BilingualNarrativeSample("en", "Foundation Notes: The foundation construction shall comply with the design drawings and applicable codes. Concrete strength grade shall be C30.", "AcDbMText"),
                new BilingualNarrativeSample("label", "JZL1(1B)", "AcDbText")
            ]);

        AssertEx.True(selection.PreferExistingEnglish);
        AssertEx.SequenceEqual(["en"], selection.ExistingEnglishIds);
        AssertEx.SequenceEqual(["cn-1", "cn-2"], selection.DuplicateChineseNarrativeIds);
    }

    public static void BilingualFixedLabelSuppressesEquivalentSeparateChineseLabel()
    {
        BilingualFixedLabelSelection selection = BilingualFixedLabelPolicy.Select(
            [
                new BilingualFixedLabelSample(
                    "cn", "图 号", "Drawing No.", "AcDbMText", "title",
                    new Rect2(10, 0, 22, 4), 3.5),
                new BilingualFixedLabelSample(
                    "en", "{\\W0.6;DRAWING NO.}", "{\\W0.6;DRAWING NO.}", "AcDbMText", "title",
                    new Rect2(24, 0, 43, 4), 3.2)
            ]);

        AssertEx.SequenceEqual(["cn"], selection.SuppressChineseIds);
        AssertEx.Equal(0, selection.MixedObjectEnglishTextById.Count);
    }

    // Break caught: existing imperative title-block English (APPROVE/CHECK/DESIGN)
    // is not recognized as equivalent to a translated past-participle label.
    public static void BilingualFixedLabelMatchesInflectedTitleBlockLabels()
    {
        BilingualFixedLabelSelection selection = BilingualFixedLabelPolicy.Select(
            [
                new BilingualFixedLabelSample(
                    "cn-approve", "\u6279 \u51C6", "Approved by", "AcDbMText", "title",
                    new Rect2(0, 20, 12, 24), 3.5),
                new BilingualFixedLabelSample(
                    "en-approve", "APPROVE", "APPROVE", "AcDbText", "title",
                    new Rect2(5, 20, 18, 24), 3.2),
                new BilingualFixedLabelSample(
                    "cn-check", "\u6821 \u5BF9", "Checked by", "AcDbMText", "title",
                    new Rect2(0, 10, 12, 14), 3.5),
                new BilingualFixedLabelSample(
                    "en-check", "CHECK", "CHECK", "AcDbText", "title",
                    new Rect2(5, 10, 18, 14), 3.2),
                new BilingualFixedLabelSample(
                    "cn-design", "\u8BBE \u8BA1", "Designed by", "AcDbMText", "title",
                    new Rect2(0, 0, 12, 4), 3.5),
                new BilingualFixedLabelSample(
                    "en-design", "DESIGN", "DESIGN", "AcDbText", "title",
                    new Rect2(5, 0, 18, 4), 3.2)
            ]);

        AssertEx.SequenceEqual(
            ["cn-approve", "cn-check", "cn-design"],
            selection.SuppressChineseIds);
    }

    // Break caught: an existing concise English title-block label is missed when
    // the generated translation uses a longer synonymous phrase.
    public static void BilingualFixedLabelMatchesTitleBlockSynonyms()
    {
        BilingualFixedLabelSelection selection = BilingualFixedLabelPolicy.Select(
            [
                new BilingualFixedLabelSample(
                    "cn-project", "\u5DE5 \u7A0B \u540D \u79F0", "Project Name", "AcDbMText", "title",
                    new Rect2(0, 30, 22, 34), 3.5),
                new BilingualFixedLabelSample(
                    "en-project", "PROJECT", "PROJECT", "AcDbText", "title",
                    new Rect2(18, 30, 35, 34), 3.2),
                new BilingualFixedLabelSample(
                    "cn-item", "\u5B50\u9879\u4EE3\u53F7\u53CA\u540D\u79F0", "Subitem Code and Name", "AcDbText", "title",
                    new Rect2(0, 20, 24, 24), 3.5),
                new BilingualFixedLabelSample(
                    "en-item", "ITEM", "ITEM", "AcDbText", "title",
                    new Rect2(20, 20, 32, 24), 3.2),
                new BilingualFixedLabelSample(
                    "cn-title", "\u56FE \u7EB8 \u540D \u79F0", "Drawing Title", "AcDbMText", "title",
                    new Rect2(0, 10, 22, 14), 3.5),
                new BilingualFixedLabelSample(
                    "en-title", "TITLE", "TITLE", "AcDbText", "title",
                    new Rect2(18, 10, 30, 14), 3.2),
                new BilingualFixedLabelSample(
                    "cn-stage", "\u9636 \u6BB5", "Phase", "AcDbMText", "title",
                    new Rect2(0, 0, 12, 4), 3.5),
                new BilingualFixedLabelSample(
                    "en-stage", "STAGE", "STAGE", "AcDbText", "title",
                    new Rect2(8, 0, 20, 4), 3.2)
            ]);

        AssertEx.SequenceEqual(
            ["cn-item", "cn-project", "cn-stage", "cn-title"],
            selection.SuppressChineseIds);
    }

    public static void BilingualFixedLabelKeepsExistingEnglishInsideMixedObject()
    {
        BilingualFixedLabelSelection selection = BilingualFixedLabelPolicy.Select(
            [
                new BilingualFixedLabelSample(
                    "mixed", "专 业 {\\W0.6;MAJOR}", "Discipline {\\W0.6;MAJOR}", "AcDbMText", "title",
                    new Rect2(10, 0, 34, 4), 3.5)
            ]);

        AssertEx.Equal(0, selection.SuppressChineseIds.Count);
        AssertEx.Equal("{\\W0.6;MAJOR}", selection.MixedObjectEnglishTextById["mixed"]);
    }

    public static void BilingualFixedLabelMatchesStackedCompanyName()
    {
        BilingualFixedLabelSelection selection = BilingualFixedLabelPolicy.Select(
            [
                new BilingualFixedLabelSample(
                    "cn", "爱吉克（河南）国际工程有限公司", "AGICO (Henan) International Engineering Co., Ltd.", "AcDbMText", "title",
                    new Rect2(0, 50, 160, 55), 4),
                new BilingualFixedLabelSample(
                    "en", "AGICO CEMENT INTERNATIONAL ENGINEERING CO., LTD.", "AGICO CEMENT INTERNATIONAL ENGINEERING CO., LTD.", "AcDbMText", "title",
                    new Rect2(0, 42, 160, 47), 4)
            ]);

        AssertEx.SequenceEqual(["cn"], selection.SuppressChineseIds);
    }

    // Break caught: a legend laid out as Chinese in the left column and existing
    // English in the right column is missed when the wording is not a literal translation.
    public static void BilingualFixedLabelLearnsRepeatedTwoColumnPairs()
    {
        BilingualFixedLabelSelection selection = BilingualFixedLabelPolicy.Select(
            [
                new BilingualFixedLabelSample(
                    "cn-fuel", "燃料", "Fuel", "AcDbText", "*Model_Space",
                    new Rect2(0, 30, 6, 34), 3.3),
                new BilingualFixedLabelSample(
                    "en-coke", "Petroleum Coke", "Petroleum Coke", "AcDbText", "*Model_Space",
                    new Rect2(36, 30, 58, 34), 3.3),
                new BilingualFixedLabelSample(
                    "cn-nitrogen", "氮气", "Nitrogen", "AcDbText", "*Model_Space",
                    new Rect2(0, 20, 6, 24), 3.3),
                new BilingualFixedLabelSample(
                    "en-nitrogen", "Nitrogen", "Nitrogen", "AcDbText", "*Model_Space",
                    new Rect2(36, 20, 49, 24), 3.3),
                new BilingualFixedLabelSample(
                    "cn-limestone", "石灰石线路", "Limestone Line", "AcDbText", "*Model_Space",
                    new Rect2(0, 10, 16, 14), 3.3),
                new BilingualFixedLabelSample(
                    "en-limestone", "Limestone Circuit", "Limestone Circuit", "AcDbText", "*Model_Space",
                    new Rect2(36, 10, 61, 14), 3.3),
                new BilingualFixedLabelSample(
                    "cn-condensate", "凝结水", "Condensate", "AcDbText", "*Model_Space",
                    new Rect2(0, 0, 10, 4), 3.3),
                new BilingualFixedLabelSample(
                    "en-condensate", "Condensate", "Condensate", "AcDbText", "*Model_Space",
                    new Rect2(36, 0, 54, 4), 3.3),
                new BilingualFixedLabelSample(
                    "unrelated", "Motor", "Motor", "AcDbText", "*Model_Space",
                    new Rect2(70, 42, 78, 46), 3.3)
            ]);

        AssertEx.SequenceEqual(
            ["cn-condensate", "cn-fuel", "cn-limestone", "cn-nitrogen"],
            selection.SuppressChineseIds);
    }

    public static void TechnicalCodeInsideChineseLabelIsNotTreatedAsBilingual()
    {
        BilingualFixedLabelSelection selection = BilingualFixedLabelPolicy.Select(
            [
                new BilingualFixedLabelSample(
                    "technical", "混凝土强度等级 C30，钢筋采用 HRB400", "Concrete grade C30; reinforcement shall be HRB400.", "AcDbMText", "notes",
                    new Rect2(0, 0, 80, 5), 3)
            ]);

        AssertEx.Equal(0, selection.MixedObjectEnglishTextById.Count);
    }

    public static void MTextFontNameAndTpdUnitAreNotTreatedAsExistingEnglish()
    {
        BilingualFixedLabelSelection selection = BilingualFixedLabelPolicy.Select(
            [
                new BilingualFixedLabelSample(
                    "title",
                    "{\\fMS PGothic|b0|i0|c0|p0;阿联酋800TPD氧化钙+240TPD氢氧化钙项目工艺流程图}",
                    "UAE 800TPD Calcium Oxide and 240TPD Calcium Hydroxide Project Process Flow Diagram",
                    "AcDbMText",
                    "*Model_Space",
                    new Rect2(0, 0, 160, 10),
                    8)
            ]);

        AssertEx.Equal(0, selection.MixedObjectEnglishTextById.Count);
    }

    public static void ChineseOnlyNarrativeStillRequiresComposition()
    {
        BilingualNarrativeSelection selection = BilingualNarrativePolicy.Select(
            [
                new BilingualNarrativeSample("cn-1", "基础施工应符合设计及规范要求，开挖后及时验槽。", "AcDbText"),
                new BilingualNarrativeSample("cn-2", "基础混凝土强度等级为C30，保护层厚度为50mm。", "AcDbText")
            ]);

        AssertEx.False(selection.PreferExistingEnglish);
        AssertEx.Equal(0, selection.ExistingEnglishIds.Count);
        AssertEx.Equal(0, selection.DuplicateChineseNarrativeIds.Count);
    }

    public static void FragmentedNarrativeDetectorFindsSplitProse()
    {
        var samples = new List<FragmentedNarrativeSample>();
        for (int row = 0; row < 8; row++)
        {
            double top = 100 - row * 5;
            samples.Add(new FragmentedNarrativeSample(
                $"{row}-a", new Rect2(10, top - 3, 28, top), "基础施工应符合", true));
            samples.Add(new FragmentedNarrativeSample(
                $"{row}-b", new Rect2(29, top - 3, 48, top), "设计及规范要求", true));
        }

        FragmentedNarrativeGroup[] groups =
            FragmentedNarrativeDetector.DetectGroups(samples, medianTextHeight: 3);

        AssertEx.Equal(1, groups.Length);
        AssertEx.Equal(8, groups[0].RowCount);
        AssertEx.Equal(16, groups[0].MemberIds.Count);
        AssertEx.True(groups[0].FragmentationRatio >= 2);
    }

    public static void NarrativeDetectorFindsContinuousSingleObjectProseColumn()
    {
        var samples = new List<FragmentedNarrativeSample>();
        for (int row = 0; row < 20; row++)
        {
            double top = 100 - row * 5;
            samples.Add(new FragmentedNarrativeSample(
                $"row-{row}",
                new Rect2(10 + row % 3, top - 3, 62, top),
                "\u57fa\u7840\u65bd\u5de5\u8bbe\u8ba1\u6280\u672f\u8981\u6c42\u8bf4\u660e",
                true));
        }

        FragmentedNarrativeGroup[] groups =
            FragmentedNarrativeDetector.DetectGroups(samples, medianTextHeight: 3);

        AssertEx.Equal(1, groups.Length);
        AssertEx.Equal(20, groups[0].RowCount);
        AssertEx.Equal(20, groups[0].MemberIds.Count);
        AssertEx.True(groups[0].FragmentationRatio < 1.5);
    }

    public static void FragmentedNarrativeDetectorRejectsTitleBlockGrid()
    {
        FragmentedNarrativeSample[] cells =
        [
            new("a1", new Rect2(0, 18, 12, 21), "项目", true),
            new("a2", new Rect2(14, 18, 26, 21), "名称", true),
            new("b1", new Rect2(0, 13, 12, 16), "设计", true),
            new("b2", new Rect2(14, 13, 26, 16), "制图", true),
            new("c1", new Rect2(0, 8, 12, 11), "审核", true),
            new("c2", new Rect2(14, 8, 26, 11), "日期", true),
            new("d1", new Rect2(0, 3, 12, 6), "图号", true),
            new("d2", new Rect2(14, 3, 26, 6), "比例", true)
        ];

        AssertEx.Equal(
            0,
            FragmentedNarrativeDetector.DetectGroups(cells, medianTextHeight: 3).Length);
    }

    public static void FragmentedNarrativeRegionExpandsToShortRowsInTheSameColumn()
    {
        FragmentedNarrativeSample[] samples =
        [
            new("seed-a", new Rect2(10, 18, 40, 21), "基础施工说明", true),
            new("seed-b", new Rect2(10, 13, 42, 16), "设计规范要求", true),
            new("short-row", new Rect2(12, 8, 24, 11), "注", true),
            new("table-cell", new Rect2(12, 3, 24, 6), "表格", false),
            new("other-column", new Rect2(70, 8, 90, 11), "其他说明", true)
        ];
        var seed = new FragmentedNarrativeGroup(
            ["seed-a", "seed-b"], new Rect2(10, 3, 42, 21), 2, 1, 12);

        string[] expanded = FragmentedNarrativeRegionExpander.Expand(seed, samples, medianTextHeight: 3);

        AssertEx.SequenceEqual(["seed-a", "seed-b", "short-row"], expanded);
    }

    public static void LogicalTextSegmentsStopAtSourceObstacleGaps()
    {
        LogicalComposedRow[] rows =
        [
            new(["a"], new Rect2(0, 18, 40, 21), "Heading", false),
            new(["b"], new Rect2(0, 13, 40, 16), "First note", false),
            new(["c"], new Rect2(0, 3, 40, 6), "Note below table", true)
        ];

        LogicalComposedRow[][] segments = LogicalTextSegmenter.SplitAtSourceGaps(rows);

        AssertEx.Equal(2, segments.Length);
        AssertEx.Equal(2, segments[0].Length);
        AssertEx.Equal(1, segments[1].Length);
    }

    public static void FragmentedNarrativeTableRowFilterPreservesProseAndSkipsDistributedCells()
    {
        FragmentedNarrativeSample[] samples =
        [
            new("note-label", new Rect2(0, 18, 4, 21), "注", true),
            new("note-colon", new Rect2(5, 18, 6, 21), ":", true),
            new("note-body", new Rect2(7, 18, 50, 21), "This is a long narrative construction requirement.", true),
            new("cell-a", new Rect2(0, 8, 10, 11), "C15", true),
            new("cell-b", new Rect2(12, 8, 22, 11), "C30", true),
            new("cell-c", new Rect2(24, 8, 34, 11), "C25", true),
            new("cell-d", new Rect2(36, 8, 46, 11), "C40", true)
        ];

        string[] kept = FragmentedNarrativeTableRowFilter.KeepNarrativeMembers(samples, medianTextHeight: 3);

        AssertEx.SequenceEqual(["note-label", "note-colon", "note-body"], kept);
    }

    public static void NarrativeCandidatePolicyReusesOnlyUnclaimedNoteColumns()
    {
        AssertEx.True(NarrativeCandidatePolicy.CanUseAuthoritativeSelector(
            "note-column-22C06-occupancy-1"));
        AssertEx.True(NarrativeCandidatePolicy.CanUseAuthoritativeSelector(
            "frame-22C06-1"));
        AssertEx.False(NarrativeCandidatePolicy.CanUseAuthoritativeSelector(
            "table-cell-1"));
        AssertEx.True(NarrativeCandidatePolicy.CanFeedGenericDetector(string.Empty, alreadyClaimed: false));
        AssertEx.True(NarrativeCandidatePolicy.CanFeedGenericDetector(
            "note-column-22C06-occupancy-1", alreadyClaimed: false));
        AssertEx.False(NarrativeCandidatePolicy.CanFeedGenericDetector(
            "note-column-2525A-occupancy-1", alreadyClaimed: true));
        AssertEx.False(NarrativeCandidatePolicy.CanFeedGenericDetector(
            "table-cell-1", alreadyClaimed: false));
        AssertEx.False(NarrativeCandidatePolicy.CanFeedGenericDetector(
            "frame-22C06-1", alreadyClaimed: false));
    }

    public static void NarrativePanelPlannerSharesVerticalEnvelopeAcrossThreeColumns()
    {
        FragmentedNarrativeGroup[] groups =
        [
            new(["left"], new Rect2(0, 0, 40, 100), 20, 1, 250),
            new(["center"], new Rect2(60, 10, 100, 90), 20, 1, 250),
            new(["right"], new Rect2(120, 20, 160, 80), 20, 1, 250)
        ];

        FragmentedNarrativeGroup[] stretched =
            NarrativePanelPlanner.StretchToSharedVerticalEnvelope(groups, medianTextHeight: 3);

        AssertEx.Equal(3, stretched.Length);
        AssertEx.True(stretched.All(group => Math.Abs(group.SourceBounds.Top - 100) < 0.001));
        AssertEx.True(stretched.All(group => Math.Abs(group.SourceBounds.Bottom) < 0.001));
    }

    public static void NarrativePanelPlannerUsesSecondPassToAddAFourthColumn()
    {
        FragmentedNarrativeGroup[] seeds =
        [
            new(["left"], new Rect2(0, 0, 40, 100), 20, 1, 250),
            new(["center"], new Rect2(60, 10, 100, 90), 20, 1, 250),
            new(["right"], new Rect2(120, 20, 160, 80), 20, 1, 250)
        ];
        FragmentedNarrativeGroup[] secondPass =
        [
            .. seeds,
            new(["center-sub-block"], new Rect2(68, 25, 96, 75), 16, 1, 210),
            new(["fourth"], new Rect2(180, 15, 220, 85), 18, 1, 220)
        ];

        FragmentedNarrativeGroup[] selected = NarrativePanelPlanner.SelectCompletePanelColumns(
            seeds,
            secondPass,
            medianTextHeight: 3);

        AssertEx.Equal(4, selected.Length);
        AssertEx.True(selected.All(group => Math.Abs(group.SourceBounds.Top - 100) < 0.001));
        AssertEx.True(selected.All(group => Math.Abs(group.SourceBounds.Bottom) < 0.001));
    }

    public static void BlockReferenceStructureIgnoresTextDependentGeometricExtents()
    {
        const string placement = "definition=25B06|position=1,2,0|rotation=0|scale=1,1,1|normal=0,0,1";

        AssertEx.Equal(
            NonTextStructureSignaturePolicy.GeometryToken(
                "AcDbBlockReference",
                placement,
                "0,0,0,100,100,0"),
            NonTextStructureSignaturePolicy.GeometryToken(
                "AcDbBlockReference",
                placement,
                "-50,-20,0,180,130,0"));
        AssertEx.False(string.Equals(
            NonTextStructureSignaturePolicy.GeometryToken("AcDbLine", string.Empty, "0,0,0,100,0,0"),
            NonTextStructureSignaturePolicy.GeometryToken("AcDbLine", string.Empty, "0,0,0,120,0,0"),
            StringComparison.Ordinal));
        AssertEx.Equal(
            NonTextStructureSignaturePolicy.GeometryToken("AcDbEllipse", string.Empty, "902.3882343574285,-1099.937271908388,0,1530.3717926609206,-588.4534325089359,0"),
            NonTextStructureSignaturePolicy.GeometryToken("AcDbEllipse", string.Empty, "902.3882343574285,-1099.937271908388,0,1530.3717926609206,-588.4534325089357,0"));
    }

    public static void TitleBlockAttributesCanScaleInPlaceToStayInsideTheirCell()
    {
        AssertEx.True(FixedLabelInPlaceScalePolicy.Allows("AcDbAttribute"));
        AssertEx.True(FixedLabelInPlaceScalePolicy.Allows("AcDbAttributeDefinition"));
        AssertEx.True(FixedLabelInPlaceScalePolicy.Allows("AcDbText"));
        AssertEx.False(FixedLabelInPlaceScalePolicy.Allows("AcDbMText"));
    }

    // Break caught: a short existing MText receives the full free-slot width,
    // so its rotated AutoCAD extents cover a large part of the drawing.
    public static void ExistingMTextWrapWidthIsNeverExpandedToTheWholeFreeSlot()
    {
        AssertEx.Equal(
            572d,
            MTextWrapWidthPolicy.Select(
                currentWidth: 572,
                allowedWidth: 58_752,
                textHeight: 30));
        AssertEx.Equal(
            400d,
            MTextWrapWidthPolicy.Select(
                currentWidth: 572,
                allowedWidth: 400,
                textHeight: 30));
    }

    // Break caught: long TextMid continuation rows are excluded from a note group,
    // then the translated left-aligned rows are reflowed directly over them.
    public static void NarrativeGroupIncludesCenterAlignedContinuationInsideItsSourceSpan()
    {
        NarrativeOccupancySample[] samples =
        [
            new("row-1", new Point2(10, 40), new Rect2(10, 38, 70, 41), "1. First long note row.", true),
            new("continuation", new Point2(40, 35), new Rect2(10, 33, 70, 36), "Centered continuation belonging to the same paragraph.", false),
            new("row-2", new Point2(10, 30), new Rect2(10, 28, 70, 31), "2. Second long note row.", true),
            new("row-3", new Point2(10, 25), new Rect2(10, 23, 70, 26), "3. Third long note row.", true),
            new("table-label", new Point2(40, 15), new Rect2(25, 13, 55, 16), "Name", false)
        ];

        NarrativeOccupancyGroup group = NarrativeOccupancyDetector.DetectGroups(
            samples,
            medianTextHeight: 3).Single();

        AssertEx.SequenceEqual(
            ["continuation", "row-1", "row-2", "row-3"],
            group.MemberIds.OrderBy(id => id));
    }

    public static void NarrativeGroupIncludesShortNumberedCenteredHeading()
    {
        NarrativeOccupancySample[] samples =
        [
            new("row-1", new Point2(10, 60), new Rect2(10, 58, 70, 61), "1. First long construction note.", true),
            new("row-2", new Point2(10, 50), new Rect2(10, 48, 70, 51), "2. Second long construction note.", true),
            new("heading", new Point2(40, 45), new Rect2(25, 43, 55, 46), "8. Foundation", false),
            new("row-3", new Point2(10, 40), new Rect2(10, 38, 70, 41), "3. Third long construction note.", true),
            new("row-4", new Point2(10, 30), new Rect2(10, 28, 70, 31), "4. Fourth long construction note.", true),
        ];

        NarrativeOccupancyGroup group = NarrativeOccupancyDetector.DetectGroups(
            samples,
            medianTextHeight: 3).Single();

        AssertEx.True(group.MemberIds.Contains("heading", StringComparer.Ordinal));
    }

    public static void NarrativeGroupIncludesShortCenteredTechnicalFragmentInsideSpan()
    {
        NarrativeOccupancySample[] samples =
        [
            new("row-1", new Point2(10, 60), new Rect2(10, 58, 70, 61), "1. First long construction note.", true),
            new("row-2", new Point2(10, 50), new Rect2(10, 48, 70, 51), "2. Second long construction note.", true),
            new("technical", new Point2(40, 45), new Rect2(30, 43, 50, 46), "%%130", false),
            new("row-3", new Point2(10, 40), new Rect2(10, 38, 70, 41), "3. Third long construction note.", true),
            new("row-4", new Point2(10, 30), new Rect2(10, 28, 70, 31), "4. Fourth long construction note.", true),
        ];

        NarrativeOccupancyGroup group = NarrativeOccupancyDetector.DetectGroups(
            samples,
            medianTextHeight: 3).Single();

        AssertEx.True(group.MemberIds.Contains("technical", StringComparer.Ordinal));
    }

    // Break caught: a note that misses the 30% readable floor is still written
    // outside its region instead of using the smaller scale needed to prevent overlap.
    public static void EmergencyTextScalingCanFitDenseEnglishWithoutCrossingTenPercent()
    {
        AssertEx.Equal(0.25d, LayoutFitPolicy.ClampEmergencyHeightScale(0.25));
        AssertEx.Equal(0.12d, LayoutFitPolicy.ClampEmergencyHeightScale(0.12));
        AssertEx.Equal(0.10d, LayoutFitPolicy.ClampEmergencyHeightScale(0.04));
    }

    // Break caught: aggregate row height fits, but an individual rotated/wrapped
    // MText still crosses the region boundary and no smaller scale is attempted.
    public static void AggregateNoteFitIsRetriedWhenAnyTextIsOutsideTheRegion()
    {
        AssertEx.True(LayoutFitPolicy.NeedsContainmentRetry(
            aggregateFits: true,
            allItemsInside: false));
        AssertEx.False(LayoutFitPolicy.NeedsContainmentRetry(
            aggregateFits: true,
            allItemsInside: true));
    }

    public static void MTextActualBoxIsMeasuredFromAttachmentAndRotation()
    {
        Rect2 bounds = TextBoundsEstimator.FromActualBox(
            new Point2(10, 20),
            width: 4,
            height: 2,
            TextAttachmentKind.TopLeft,
            Math.PI / 2);

        AssertEx.True(Math.Abs(10 - bounds.Left) < 1e-9);
        AssertEx.True(Math.Abs(20 - bounds.Bottom) < 1e-9);
        AssertEx.True(Math.Abs(12 - bounds.Right) < 1e-9);
        AssertEx.True(Math.Abs(24 - bounds.Top) < 1e-9);

        Rect2 centered = TextBoundsEstimator.FromActualBox(
            new Point2(5, 6),
            width: 8,
            height: 4,
            TextAttachmentKind.MiddleCenter,
            rotationRadians: 0);
        AssertEx.True(Math.Abs(1 - centered.Left) < 1e-9);
        AssertEx.True(Math.Abs(4 - centered.Bottom) < 1e-9);
        AssertEx.True(Math.Abs(9 - centered.Right) < 1e-9);
        AssertEx.True(Math.Abs(8 - centered.Top) < 1e-9);
    }

    public static void GlobalCollisionCorrectionSelectsOnlyAdjustedSidesOfHighTextPairs()
    {
        LayoutCorrectionCandidate[] risks =
        [
            new("changed-a", "unchanged-a", LayoutRiskCode.TextOverlap, LayoutRiskLevel.High),
            new("changed-b", "changed-c", LayoutRiskCode.TextOverlap, LayoutRiskLevel.High),
            new("changed-d", "unchanged-d", LayoutRiskCode.TextOverlap, LayoutRiskLevel.Medium),
            new("changed-e", null, LayoutRiskCode.CrossRegion, LayoutRiskLevel.High)
        ];

        string[] selected = LayoutCorrectionPolicy.SelectAdjustedTextOverlapRecords(
            risks,
            ["changed-a", "changed-b", "changed-c", "changed-d", "changed-e"]);

        AssertEx.SequenceEqual(["changed-a", "changed-b", "changed-c"], selected);
    }

    public static void StackedTableTextsReceiveVerticallyExclusiveBoxes()
    {
        IReadOnlyDictionary<string, Rect2> boxes = ExclusiveTextBoxAllocator.Allocate(
            new Rect2(0, 0, 20, 20),
            [
                new LayoutTextBoxSample("upper", new Rect2(5, 12, 15, 16), 4),
                new LayoutTextBoxSample("lower", new Rect2(5, 4, 15, 8), 4)
            ]);

        AssertEx.True(boxes["lower"].Top < boxes["upper"].Bottom);
        AssertEx.True(boxes.Values.All(box => box.Width > 0 && box.Height > 0));
    }

    public static void ReopenedCandidateBoundsDriveConservativeCorrectionScale()
    {
        double scale = LayoutFitPolicy.AuditCorrectionScale(
            candidateWidth: 1000,
            candidateHeight: 100,
            allowedWidth: 300,
            allowedHeight: 200);

        AssertEx.True(Math.Abs(0.27 - scale) < 1e-9);
        AssertEx.Equal(1d, LayoutFitPolicy.AuditCorrectionScale(100, 100, 300, 200));
        AssertEx.Equal(0.10d, LayoutFitPolicy.AuditCorrectionScale(10000, 10000, 100, 100));
    }

    public static void AnchoredAvailableSizeRespectsTheNearestSlotBoundary()
    {
        (double width, double height) = TextBoundsEstimator.AvailableSizeFromAnchor(
            new Rect2(0, 0, 100, 40),
            new Point2(70, 10),
            TextAttachmentKind.BottomLeft);
        AssertEx.Equal(30d, width);
        AssertEx.Equal(30d, height);

        (double centeredWidth, double centeredHeight) = TextBoundsEstimator.AvailableSizeFromAnchor(
            new Rect2(0, 0, 100, 40),
            new Point2(70, 10),
            TextAttachmentKind.MiddleCenter);
        AssertEx.Equal(60d, centeredWidth);
        AssertEx.Equal(20d, centeredHeight);
    }

    public static void ReopenedFeedbackRestartsFromSourceHeightWithReadableFloor()
    {
        AssertEx.Equal(320d, LayoutCorrectionPolicy.RestoreSourceTextHeight(96, 320));
        AssertEx.Equal(160d, LayoutCorrectionPolicy.MinimumReadableHeight(320));
    }

    public static void InlineMeasurementPaddingReservesRenderedInkSafety()
    {
        AssertEx.Equal(103d, NarrativeInlinePacker.MeasurementSafeWidth(100));
    }

    public static void CorrectionRegionKeepsTheSourceVerticalEnvelope()
    {
        Rect2 constrained = NarrativeOccupancyEnvelope.ConstrainToSourceVerticalEnvelope(
            new Rect2(0, -20, 100, 120),
            [new Rect2(10, 0, 80, 20), new Rect2(20, 80, 90, 100)]);
        AssertEx.Equal(new Rect2(0, 0, 100, 100), constrained);
    }

    public static void Sha256IsLowerHex()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cadtrans-hash-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "abc");
            AssertEx.Equal(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                Hashing.Sha256File(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static void AtomicWriteReplacesCompleteFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cadtrans-atomic-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "result.jsonl");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "old");
            AtomicFile.WriteUtf8(path, "new complete content");
            AssertEx.Equal("new complete content", File.ReadAllText(path));
            AssertEx.Equal(0, Directory.GetFiles(directory, ".result.jsonl.*.tmp").Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static void MTextFieldsAndCodesAreProtected()
    {
        var parsed = ProtectedText.Parse(@"{\f宋体|b0|i0|c0|p0;\H1.5x;\W0.8x;\FArial|b0|i0;\C1;尺寸 %%d %%p %%c Ø50 1,25 MPa 20毫米 20N·m A-20 {nested {value text}} {name} ${name} %s %1\P%<\AcVar ctab>%}");
        string[] protectedRaw = parsed.ProtectedTokens.Select(x => x.Raw).ToArray();

        foreach (string expected in new[]
        {
            @"\f宋体|b0|i0|c0|p0;", @"\H1.5x;", @"\W0.8x;", @"\FArial|b0|i0;", @"\C1;", "%%d", "%%p", "%%c",
            "Ø50", "1,25 MPa", "20毫米", "20N·m", "A-20", "{name}", "${name}", "%s", "%1", @"\P", @"%<\AcVar ctab>%"
        })
        {
            AssertEx.Contains(expected, protectedRaw);
        }

        AssertEx.True(parsed.PlainText.Contains("尺寸", StringComparison.Ordinal));
        AssertEx.False(parsed.PlainText.Contains("毫米", StringComparison.Ordinal));
        AssertEx.Equal(6, parsed.ProtectedTokens.Count(token => token.Kind == "mtext-brace"));
        AssertEx.Throws<FormatException>(() => ProtectedText.Parse(@"{\C1;未闭合"));
    }

    // Break caught: identity MTEXT loses grouping braces when translations are restored for import.
    public static void MTextGroupBracesAreProtectedAndRoundTripExactly()
    {
        const string raw = @"{\C1;Outer {nested text} {name} ${name}}";
        ParsedText parsed = ProtectedText.Parse(raw);
        ProtectedToken[] braces = parsed.ProtectedTokens.Where(token => token.Kind == "mtext-brace").ToArray();

        AssertEx.Equal(4, braces.Length);
        AssertEx.True(braces.All(token => token.Raw is "{" or "}"));
        AssertEx.Contains("{name}", parsed.ProtectedTokens.Select(token => token.Raw));
        AssertEx.Contains("${name}", parsed.ProtectedTokens.Select(token => token.Raw));
        AssertEx.Equal(raw, TranslationValidator.RestoreProtectedTokens(parsed.PlainText, parsed.ProtectedTokens));
    }

    // Break caught: Chinese units and font names were hidden inside protected tokens,
    // so a visibly English drawing still contained Chinese in its raw CAD text.
    public static void OutputRestorationLocalizesProtectedChineseUnitsAndFontNames()
    {
        const string raw = @"{\F宋体|c134;标高 2.0米，烈度 7度}";
        ParsedText parsed = ProtectedText.Parse(raw);
        string[] markers = parsed.ProtectedTokens.Select(token => token.Marker).ToArray();
        string translated = $"{markers[0]}{markers[1]}Elevation {markers[2]}, Intensity {markers[3]}{markers[4]}";

        string output = TranslationValidator.RestoreProtectedTokensForOutput(translated, parsed.ProtectedTokens);

        AssertEx.Equal(@"{\FSimSun|c134;Elevation 2.0m, Intensity 7°}", output);
        AssertEx.True(TranslationValidator.HasSameInvariantTokens(raw, output));
    }

    // Break caught: a protected number-unit such as "100 吨" was restored as
    // "100 t", which the invariant tokenizer read as a bare 100 and rejected.
    public static void OutputRestorationCompactsWhitespaceBeforeChineseUnits()
    {
        var token = new ProtectedToken("⟦P0001⟧", "number-unit", "100 吨");

        ProtectedToken output = TranslationValidator.NormalizeProtectedTokenForOutput(token);

        AssertEx.Equal("100t", output.Raw);
        AssertEx.True(TranslationValidator.HasSameInvariantTokens("日产 100 吨", "Capacity 100t/d".Replace("/d", string.Empty, StringComparison.Ordinal)));
    }

    public static void TranslationRejectsChangedNumbers()
    {
        var parsed = ProtectedText.Parse("压力 1.6MPa，扭矩 20N·m，长度 20毫米，型号 A-20");
        var manifest = new ManifestRecord(
            "1.0", "record-1", "file-hash", "Model", "1A", "TEXT", "TextString", "content",
            "压力 1.6MPa，扭矩 20N·m，长度 20毫米，型号 A-20", parsed.PlainText, parsed.FormatTemplate, parsed.ProtectedTokens,
            new TextGeometry(new Point3Snapshot(0, 0, 0), null, 0, null),
            new TextProperties("0", "Standard", 1, 1, "Left", "Baseline", new Dictionary<string, string>()),
            "input-hash");
        string[] markers = parsed.ProtectedTokens.Select(x => x.Marker).ToArray();
        var translation = new TranslationRecord("1.0", "record-1", "input-hash", string.Join(" ", markers), "approved", "");

        AssertEx.True(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation }).IsValid);

        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest with { RecordId = null! } }, new[] { translation }), "missing_record_id");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest, manifest with { Handle = "1B" } }, new[] { translation }), "duplicate_record_id");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation with { RecordId = "unknown" } }), "unknown_record_id");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation, translation }), "duplicate_translation_record_id");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation with { InputHash = "changed" } }), "input_hash_mismatch");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation with { TranslatedText = string.Empty } }), "empty_translation");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation with { TranslatedText = string.Join(" ", markers.Skip(1)) } }), "protected_marker_mismatch");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation with { TranslatedText = string.Join(" ", markers.Append(markers[0])) } }), "protected_marker_mismatch");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation with { TranslatedText = string.Join(" ", markers.Reverse()) } }), "protected_marker_mismatch");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation with { TranslatedText = string.Join(" ", markers.Prepend(markers[0].Replace("0001", "9999", StringComparison.Ordinal))) } }), "protected_marker_mismatch");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation with { ReviewStatus = "draft" } }), "invalid_review_status");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation with { RecordId = null! } }), "missing_translation_record_id");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation with { InputHash = null! } }), "missing_input_hash");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation with { TranslatedText = null! } }), "missing_translated_text");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { translation with { ReviewStatus = null! } }), "missing_review_status");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest with { ProtectedTokens = null! } }, new[] { translation }), "malformed_protected_tokens");
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest with { ProtectedTokens = new ProtectedToken[] { null! } } }, new[] { translation }), "malformed_protected_tokens");

        var changedTextUnit = translation with { TranslatedText = "Pressure 1.6MPa torque 20N·cm length 20毫米 model A-20" };
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { changedTextUnit }), "numeric_or_protected_token_mismatch");
    }

    // Break caught: an enumerated item such as "1 General note" was parsed as
    // the numeric token "1GENERAL", so valid English translations were rejected.
    public static void InvariantTokenizerDoesNotTreatTranslatedWordsAsUnits()
    {
        AssertEx.True(TranslationValidator.HasSameInvariantTokens("1、说明", "1 General note"));
        AssertEx.False(TranslationValidator.HasSameInvariantTokens("压力 1.6MPa", "Pressure 1.6kPa"));
        AssertEx.True(TranslationValidator.HasSameInvariantTokens("型号 GB50010", "Model GB50010"));
    }

    // Break caught: the P in AutoCAD's MTEXT paragraph code (\P) was joined to
    // the following number or equipment tag and misread as model data.
    public static void InvariantTokenizerIgnoresMTextParagraphCodes()
    {
        AssertEx.True(TranslationValidator.HasSameInvariantTokens(@"\P1、模式", @"\P 1 Mode"));
        AssertEx.True(TranslationValidator.HasSameInvariantTokens(@"\PZDV004 全开", @"\P ZDV004 fully open"));
        AssertEx.True(TranslationValidator.HasSameInvariantTokens(@"\PBV021/BV022", @"\P BV021/BV022"));
    }

    // Break caught: an unchanged source label already outside an inferred cell
    // was reported as a new cross-region translation failure.
    public static void CrossRegionAuditIgnoresUnchangedPreexistingOverflow()
    {
        AssertEx.False(LayoutCrossRegionPolicy.ShouldReport(
            isChanged: false,
            sourceInsideRegion: false,
            candidateInsideRegion: false));
        AssertEx.True(LayoutCrossRegionPolicy.ShouldReport(
            isChanged: true,
            sourceInsideRegion: false,
            candidateInsideRegion: false));
        AssertEx.True(LayoutCrossRegionPolicy.ShouldReport(
            isChanged: false,
            sourceInsideRegion: true,
            candidateInsideRegion: false));
    }

    // Break caught: a manifest exported by the old parser can silently drop MTEXT grouping braces on import.
    public static void TranslationRejectsManifestThatCannotRoundTripRawText()
    {
        (ManifestRecord manifest, TranslationRecord translation) = CandidateFixture();
        ManifestRecord malformed = manifest with
        {
            RawText = "{identity}",
            PlainText = "identity",
            ProtectedTokens = Array.Empty<ProtectedToken>()
        };

        AssertInvalid(TranslationValidator.ValidateBatch(new[] { malformed }, new[] { translation with { TranslatedText = "identity" } }), "manifest_text_roundtrip_mismatch");
    }

    public static void DimensionPlaceholdersAndCodesAreProtected()
    {
        const string raw = @"<>\X中文";
        ParsedText parsed = ProtectedText.Parse(raw);
        AssertEx.Contains("<>", parsed.ProtectedTokens.Select(token => token.Raw));
        AssertEx.Contains(@"\X", parsed.ProtectedTokens.Select(token => token.Raw));
        AssertEx.True(parsed.PlainText.Contains("中文", StringComparison.Ordinal));
        AssertEx.False(parsed.PlainText.Contains("<>", StringComparison.Ordinal));
        AssertEx.False(parsed.PlainText.Contains(@"\X", StringComparison.Ordinal));

        var manifest = new ManifestRecord("1.0", "dimension-1", "file", "ROOT/BLOCK/Model/1", "1", "DIMENSION", "override", "dimension-override",
            raw, parsed.PlainText, parsed.FormatTemplate, parsed.ProtectedTokens,
            new TextGeometry(new Point3Snapshot(0, 0, 0), null, 0, null),
            new TextProperties("0", "", 0, 1, "", "", new Dictionary<string, string>()), "input");
        string accepted = string.Join(" translated ", parsed.ProtectedTokens.Select(token => token.Marker));
        AssertEx.True(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { new TranslationRecord("1.0", "dimension-1", "input", accepted, "approved", "") }).IsValid);
        AssertInvalid(TranslationValidator.ValidateBatch(new[] { manifest }, new[] { new TranslationRecord("1.0", "dimension-1", "input", parsed.ProtectedTokens[0].Marker, "approved", "") }), "protected_marker_mismatch");
    }

    // Break caught: an importer writing marker text, or restoring a protected token differently from export.
    public static void ApprovedTranslationRestoresProtectedTokensExactly()
    {
        ParsedText parsed = ProtectedText.Parse(@"\C1;阀门\P%<\AcVar ctab>%");
        string translated = $"Valve {string.Concat(parsed.ProtectedTokens.Select(token => token.Marker))}";

        AssertEx.Equal(@"Valve \C1;\P%<\AcVar ctab>%", TranslationValidator.RestoreProtectedTokens(translated, parsed.ProtectedTokens));
    }

    // Break caught: a stale or forged manifest redirects a valid slot onto a different AutoCAD entity class.
    public static void ManifestTargetTypeRequiresExactRxClassMatch()
    {
        AssertEx.True(ImportTargetContract.HasExactObjectType("AcDbAttributeDefinition", "AcDbAttributeDefinition"));
        AssertEx.False(ImportTargetContract.HasExactObjectType("AcDbAttributeDefinition", "AcDbAttribute"));
        AssertEx.False(ImportTargetContract.HasExactObjectType("AcDbMText", "AcDbText"));
        AssertEx.False(ImportTargetContract.HasExactObjectType("AcDbRotatedDimension", "AcDbAlignedDimension"));
    }

    // Break caught: a candidate writer changes marker-restored text, object class, or slot after import.
    public static void CandidateContentRequiresExactRestoredValueAndIdentity()
    {
        (ManifestRecord manifest, TranslationRecord translation) = CandidateFixture();
        var candidate = new CandidateTextRecord("candidate-1", "1A", "AcDbText", "text", "Valve \\C1;");

        VerificationResult result = CandidateContentVerifier.Verify(new[] { manifest }, new[] { translation }, new[] { candidate });

        AssertEx.True(result.IsValid);
        AssertEx.False(CandidateContentVerifier.Verify(new[] { manifest }, new[] { translation }, new[] { candidate with { ObjectType = "AcDbMText" } }).IsValid);
        AssertEx.False(CandidateContentVerifier.Verify(new[] { manifest }, new[] { translation }, new[] { candidate with { Slot = "contents" } }).IsValid);
        AssertEx.False(CandidateContentVerifier.Verify(new[] { manifest }, new[] { translation }, new[] { candidate with { ActualText = "Valve P0001" } }).IsValid);
    }

    // Break caught: a verifier silently accepts an omitted, duplicated, or stale candidate record.
    public static void CandidateContentRejectsMissingDuplicateAndStaleRecords()
    {
        (ManifestRecord manifest, TranslationRecord translation) = CandidateFixture();
        var candidate = new CandidateTextRecord("candidate-1", "1A", "AcDbText", "text", "Valve \\C1;");

        AssertVerificationInvalid(CandidateContentVerifier.Verify(new[] { manifest }, new[] { translation }, Array.Empty<CandidateTextRecord>()), "candidate_record_missing");
        AssertVerificationInvalid(CandidateContentVerifier.Verify(new[] { manifest }, new[] { translation }, new[] { candidate, candidate }), "candidate_duplicate_record_id");
        AssertVerificationInvalid(CandidateContentVerifier.Verify(new[] { manifest }, new[] { translation }, new[] { candidate with { Handle = "1B" } }), "candidate_handle_mismatch");
    }

    // Break caught: a malformed manifest makes verification crash before it writes stable failure codes.
    public static void CandidateContentReturnsManifestValidationErrorsWithoutThrowing()
    {
        (ManifestRecord manifest, TranslationRecord translation) = CandidateFixture();
        var candidate = new CandidateTextRecord("candidate-1", "1A", "AcDbText", "text", "Valve \\C1;");

        VerificationResult result = CandidateContentVerifier.Verify(new[] { manifest with { ProtectedTokens = null! } }, new[] { translation }, new[] { candidate });

        AssertVerificationInvalid(result, "malformed_protected_tokens");
    }

    // Break caught: a translator injects new MTEXT controls or fields that are absent from the manifest token list.
    public static void CandidateContentRejectsInjectedProtectedControlAndFieldTokens()
    {
        (ManifestRecord manifest, TranslationRecord translation) = CandidateFixture();
        string marker = manifest.ProtectedTokens.Single().Marker;
        foreach (string injected in new[] { @"\C2;", @"%<\AcVar ctab>%" })
        {
            TranslationRecord injectedTranslation = translation with { TranslatedText = $"Valve {marker}{injected}" };
            string candidateText = TranslationValidator.RestoreProtectedTokens(injectedTranslation.TranslatedText, manifest.ProtectedTokens);
            var candidate = new CandidateTextRecord("candidate-1", "1A", "AcDbText", "text", candidateText);

            AssertVerificationInvalid(CandidateContentVerifier.Verify(new[] { manifest }, new[] { injectedTranslation }, new[] { candidate }), "candidate_protected_token_mismatch");
        }
    }

    // Break caught: a wrong candidate number is reported only as generic text mismatch, without source-invariant evidence.
    public static void CandidateContentComparesNumericInvariantsToManifestSource()
    {
        (ManifestRecord manifest, TranslationRecord translation) = CandidateFixture();
        var candidate = new CandidateTextRecord("candidate-1", "1A", "AcDbText", "text", "Valve \\C1; 2mm");

        AssertVerificationInvalid(CandidateContentVerifier.Verify(new[] { manifest }, new[] { translation }, new[] { candidate }), "candidate_invariant_token_mismatch");
    }

    // Break caught: a candidate changes a translated entity's geometry or display properties while retaining its text.
    public static void TextStructureSignatureRejectsPropertyChangesButNotTextContent()
    {
        var baseline = new TextStructureSignature("1A", "AcDbText", "ROOT/BLOCK/Model/1A", "notes", "ByLayer", "LineWeight050",
            "position=1,2,0|alignment=1,2,0|normal=0,0,1|rotation=0|height=2.5|width=1|oblique=0|style=Standard");
        string expected = DrawingStructureSignature.Compute(new[] { "entity|2B|AcDbLine|ROOT/BLOCK/Model/2B|0|ByLayer|LineWeight000|0,0,0,1,1,0" }, new[] { baseline });

        AssertEx.Equal(expected, DrawingStructureSignature.Compute(new[] { "entity|2B|AcDbLine|ROOT/BLOCK/Model/2B|0|ByLayer|LineWeight000|0,0,0,1,1,0" }, new[] { baseline }));
        AssertEx.False(string.Equals(expected, DrawingStructureSignature.Compute(new[] { "entity|2B|AcDbLine|ROOT/BLOCK/Model/2B|0|ByLayer|LineWeight000|0,0,0,1,1,0" }, new[] { baseline with { Layer = "changed" } }), StringComparison.Ordinal));
        AssertEx.False(string.Equals(expected, DrawingStructureSignature.Compute(new[] { "entity|2B|AcDbLine|ROOT/BLOCK/Model/2B|0|ByLayer|LineWeight000|0,0,0,1,1,0" }, new[] { baseline with { Color = "Red" } }), StringComparison.Ordinal));
        AssertEx.False(string.Equals(expected, DrawingStructureSignature.Compute(new[] { "entity|2B|AcDbLine|ROOT/BLOCK/Model/2B|0|ByLayer|LineWeight000|0,0,0,1,1,0" }, new[] { baseline with { Properties = "position=9,2,0|alignment=1,2,0|normal=0,0,1|rotation=0|height=2.5|width=1|oblique=0|style=Standard" } }), StringComparison.Ordinal));
    }

    // Break caught: importer opens every resolved object ForWrite, including identity translations.
    public static void ImportWriteDecisionSkipsIdentityAndSelectsOnlyChangedRecords()
    {
        AssertEx.False(ImportWriteDecision.NeedsWrite("unchanged", "unchanged"));
        AssertEx.True(ImportWriteDecision.NeedsWrite("source", "translated"));

        var records = new[]
        {
            (Current: "same", Restored: "same"),
            (Current: "source", Restored: "translated"),
            (Current: "also-same", Restored: "also-same")
        };
        AssertEx.Equal(1, records.Count(record => ImportWriteDecision.NeedsWrite(record.Current, record.Restored)));
    }

    // Break caught: font/unit normalization inside protected markers was treated
    // as visible translation growth and shrank unchanged numeric labels to 10%.
    public static void ImportLayoutDecisionSkipsProtectedOnlyOutputNormalization()
    {
        const string sourcePlainText = "⟦P0001⟧⟦P0002⟧⟦P0003⟧⟦P0004⟧";
        AssertEx.False(ImportWriteDecision.NeedsLayout(sourcePlainText, sourcePlainText));
    }

    private static (ManifestRecord Manifest, TranslationRecord Translation) CandidateFixture()
    {
        ParsedText parsed = ProtectedText.Parse(@"\\C1;阀门");
        var manifest = new ManifestRecord("1.0", "candidate-1", "file", "ROOT/BLOCK/Model/1A", "1A", "AcDbText", "text", "text",
            @"\\C1;阀门", parsed.PlainText, parsed.FormatTemplate, parsed.ProtectedTokens,
            new TextGeometry(new Point3Snapshot(0, 0, 0), null, 0, null),
            new TextProperties("0", "Standard", 1, 1, "Left", "Baseline", new Dictionary<string, string>()), "candidate-input");
        string translated = $"Valve {parsed.ProtectedTokens.Single().Marker}";
        return (manifest, new TranslationRecord("1.0", "candidate-1", "candidate-input", translated, "approved", ""));
    }

    // Break caught: CADTRANS_VERIFY remains a successful no-op instead of enforcing its verification gate.
    public static void VerifyCommandIsWiredToDrawingVerifier()
    {
        string root = Directory.GetCurrentDirectory();
        string commands = File.ReadAllText(Path.Combine(root, "src", "cad", "CadTranslation.AutoCAD2025", "Commands.cs"));
        string verifier = File.ReadAllText(Path.Combine(root, "src", "cad", "CadTranslation.AutoCAD2025", "DrawingVerifier.cs"));

        AssertEx.True(commands.Contains("DrawingVerifier.Verify", StringComparison.Ordinal));
        AssertEx.True(verifier.Contains("CandidateContentVerifier.Verify", StringComparison.Ordinal));
        AssertEx.True(verifier.Contains("AtomicFile.WriteUtf8", StringComparison.Ordinal));
        AssertEx.True(verifier.Contains("TextStructureSignature", StringComparison.Ordinal));
        AssertEx.True(verifier.Contains("DBText", StringComparison.Ordinal));
        AssertEx.True(verifier.Contains("MText", StringComparison.Ordinal));
        AssertEx.True(verifier.Contains("Dimension", StringComparison.Ordinal));
    }

    public static void ComposeCommandIsWiredToLogicalFlowPrototype()
    {
        string root = Directory.GetCurrentDirectory();
        string commands = File.ReadAllText(Path.Combine(root, "src", "cad", "CadTranslation.AutoCAD2025", "Commands.cs"));
        string runner = File.ReadAllText(Path.Combine(root, "tests", "cad", "integration", "run_coreconsole.py"));
        string composer = File.ReadAllText(Path.Combine(root, "src", "cad", "CadTranslation.AutoCAD2025", "LogicalFlowPrototype.cs"));

        AssertEx.True(commands.Contains("CADTRANS_COMPOSE", StringComparison.Ordinal));
        AssertEx.True(commands.Contains("LogicalFlowPrototype.Run", StringComparison.Ordinal));
        AssertEx.True(runner.Contains("\"compose\": \"CADTRANS_COMPOSE\"", StringComparison.Ordinal));
        AssertEx.True(composer.Contains("FragmentedNarrativeDetector.DetectGroups", StringComparison.Ordinal));
        AssertEx.True(composer.Contains("NarrativePanelPlanner.SelectCompletePanelColumns", StringComparison.Ordinal));
        AssertEx.True(composer.Contains("NarrativeCandidatePolicy.CanFeedGenericDetector", StringComparison.Ordinal));
        AssertEx.True(composer.Contains("seedGroups.Length >= 3", StringComparison.Ordinal));
        AssertEx.False(composer.Contains("MinimumDenseRegionRecords", StringComparison.Ordinal));
    }

    // Break caught: a structural mismatch short-circuits candidate content verification and hides text evidence.
    public static void VerifyStructureErrorsStillReadCandidateContent()
    {
        string root = Directory.GetCurrentDirectory();
        string verifier = File.ReadAllText(Path.Combine(root, "src", "cad", "CadTranslation.AutoCAD2025", "DrawingVerifier.cs"));

        AssertEx.True(verifier.Contains("AddStructureError(source, candidate", StringComparison.Ordinal));
        AssertEx.True(verifier.IndexOf("AddStructureError(source, candidate", StringComparison.Ordinal) < verifier.IndexOf("CandidateTextRecord[] candidateRecords", StringComparison.Ordinal));
        AssertEx.True(verifier.Contains("componentSignatures", StringComparison.Ordinal));
        AssertEx.True(verifier.Contains("FirstDifference", StringComparison.Ordinal));
    }

    // Break caught: a rejected verify config/hash preflight writes only an envelope and loses its verification artifact.
    public static void VerifyPreflightFailureHasSafeAtomicArtifactRoute()
    {
        string root = Directory.GetCurrentDirectory();
        string commands = File.ReadAllText(Path.Combine(root, "src", "cad", "CadTranslation.AutoCAD2025", "Commands.cs"));
        string context = File.ReadAllText(Path.Combine(root, "src", "cad", "CadTranslation.AutoCAD2025", "JobContext.cs"));

        AssertEx.True(commands.Contains("TryWriteVerificationFailure", StringComparison.Ordinal));
        AssertEx.True(context.Contains("TryWriteVerificationFailure", StringComparison.Ordinal));
        AssertEx.True(context.Contains("AtomicFile.WriteUtf8", StringComparison.Ordinal));
        AssertEx.True(context.Contains("IsWithin(jobRoot, artifactDirectory)", StringComparison.Ordinal));
    }

    // Break caught: the packaged skill places config under job/config, but the plugin treats config as the job root.
    public static void JobConfigDirectoryResolvesToItsParentJobRoot()
    {
        string job = Path.Combine(Path.GetTempPath(), "cadtrans-job");
        AssertEx.Equal(job, JobPathPolicy.ResolveJobRoot(Path.Combine(job, "config", "export-job.json")));
        AssertEx.Equal(job, JobPathPolicy.ResolveJobRoot(Path.Combine(job, "import-job.json")));
        AssertEx.Equal(Path.Combine(job, "other"), JobPathPolicy.ResolveJobRoot(Path.Combine(job, "other", "job.json")));
    }

    public static void OutputModePolicyNormalizesLegacyEnglish()
    {
        AssertEx.Equal("replace", OutputModePolicy.Normalize("english"));
        AssertEx.Equal("replace", OutputModePolicy.Normalize(null));
        AssertEx.Equal("bilingual", OutputModePolicy.Normalize(" BILINGUAL "));
    }

    public static void OutputModePolicyRejectsUnknownMode()
    {
        AssertEx.Throws<ArgumentException>(() => OutputModePolicy.Normalize("mixed"));
    }

    // Break caught: the optimizer shrinks English text before trying word wrapping.
    public static void LayoutFitWrapsBeforeCompressingOrShrinking()
    {
        LayoutFitDecision decision = LayoutFitPolicy.Decide(new LayoutFitRequest(
            SourceWidth: 100, SourceHeight: 20, CandidateWidth: 180, CandidateHeight: 20,
            OriginalWidthFactor: 1, OriginalTextHeight: 10, CanWrap: true,
            WrappedWidth: 100, WrappedHeight: 32));

        AssertEx.Equal(LayoutAction.Wrap, decision.Actions[0]);
        AssertEx.Equal(1.0, decision.WidthFactor);
        AssertEx.True(decision.TextHeight < 10);
        AssertEx.False(decision.ManualReview);
    }

    // Break caught: a difficult line is made unreadably narrow or small to force a fit.
    public static void LayoutFitRespectsWidthAndHeightFloors()
    {
        LayoutFitDecision decision = LayoutFitPolicy.Decide(new LayoutFitRequest(
            SourceWidth: 100, SourceHeight: 20, CandidateWidth: 500, CandidateHeight: 20,
            OriginalWidthFactor: 1, OriginalTextHeight: 10, CanWrap: false,
            WrappedWidth: 500, WrappedHeight: 20));

        AssertEx.True(decision.WidthFactor >= 0.70);
        AssertEx.True(decision.TextHeight >= 5.50);
        AssertEx.True(decision.NeedsTextReflow);
        AssertEx.True(decision.ManualReview);
    }

    public static void FixedLabelsNeverUseEmergencyTenPercentHeight()
    {
        AssertEx.Equal(0.55d, LayoutFitPolicy.ClampReadableHeightScale(0.10));
        AssertEx.Equal(0.75d, LayoutFitPolicy.ClampReadableHeightScale(0.75));
    }

    // Break caught: titles, attributes, centered labels, and short model labels are converted to MTEXT.
    public static void NarrativeClassifierConvertsOnlyLongLeftAlignedDbText()
    {
        AssertEx.True(NarrativeTextClassifier.IsConvertibleDbText(new LayoutTextProfile(
            "AcDbText", "TextLeft", "1. Provide reinforced concrete foundations in accordance with the structural notes.")));
        AssertEx.True(NarrativeTextClassifier.IsConvertibleDbText(new LayoutTextProfile(
            "AcDbText", "TextLeft", "Characteristic uniformly distributed live loads applied to the design")));
        AssertEx.False(NarrativeTextClassifier.IsConvertibleDbText(new LayoutTextProfile(
            "AcDbText", "TextCenter", "1. Provide reinforced concrete foundations in accordance with the structural notes.")));
        AssertEx.False(NarrativeTextClassifier.IsConvertibleDbText(new LayoutTextProfile(
            "AcDbAttribute", "TextLeft", "1. Provide reinforced concrete foundations in accordance with the structural notes.")));
        AssertEx.False(NarrativeTextClassifier.IsConvertibleDbText(new LayoutTextProfile(
            "AcDbText", "TextLeft", "Roof Plan")));
        AssertEx.False(NarrativeTextClassifier.IsConvertibleDbText(new LayoutTextProfile(
            "AcDbText", "TextLeft", "C30 8@200 GB50010")));
    }

    public static void LayoutTextMetricsEstimateCjkAndLatinWidths()
    {
        AssertEx.Equal(6d, LayoutTextMetrics.EstimateEmWidth("结构设计说明"));
        AssertEx.True(LayoutTextMetrics.EstimateEmWidth("Structural Design Notes") > 10d);
    }

    // Break caught: harmless source contact is reported as a translation collision,
    // or a newly introduced overlap above ten percent is missed.
    public static void LayoutCollisionFlagsOnlyNewSevereOverlap()
    {
        AssertEx.True(LayoutCollision.IsNewSevereOverlap(
            sourceOverlapArea: 0, candidateOverlapArea: 24, smallerCandidateArea: 100));
        AssertEx.False(LayoutCollision.IsNewSevereOverlap(
            sourceOverlapArea: 15, candidateOverlapArea: 18, smallerCandidateArea: 100));
        AssertEx.False(LayoutCollision.IsNewSevereOverlap(
            sourceOverlapArea: 0, candidateOverlapArea: 9, smallerCandidateArea: 100));
    }

    // Break caught: table text is classified by word count because the surrounding grid is never recognized.
    public static void MergedCellIgnoresTicksFromNeighboringRows()
    {
        Segment2[] lines = {
            new(new(0, 0), new(60, 0)), new(new(0, 15), new(60, 15)),
            new(new(0, 0), new(0, 30)), new(new(60, 0), new(60, 30)),
            new(new(20, 15), new(20, 30)), new(new(40, 15), new(40, 30)),
            new(new(0, 30), new(60, 30)) };
        var cell = GridCellDetector.DetectContaining(lines, new Rect2(5, 5, 55, 10));
        AssertEx.True(cell is not null);
        AssertEx.Equal(new Rect2(0, 0, 60, 15), cell!.Bounds);
        AssertEx.True(GridCellDetector.DetectContaining(lines.Take(1).ToArray(), new Rect2(5, 5, 55, 10)) is null);
    }

    public static void GridLinesCreateATableCell()
    {
        LayoutRegion[] cells = GridCellDetector.Detect(
        [
            new Segment2(new Point2(0, 0), new Point2(100, 0)),
            new Segment2(new Point2(0, 20), new Point2(100, 20)),
            new Segment2(new Point2(0, 0), new Point2(0, 20)),
            new Segment2(new Point2(100, 0), new Point2(100, 20))
        ]);

        AssertEx.Equal(1, cells.Length);
        AssertEx.Equal(LayoutRegionKind.TableCell, cells[0].Kind);
        AssertEx.Equal(new Rect2(0, 0, 100, 20), cells[0].Bounds);
    }

    // Break caught: a table-cell text is assigned to its enclosing note frame and is reflowed across rows.
    public static void RegionAssignmentSelectsTheSmallestTrustworthyRegion()
    {
        var text = new TextLayoutSnapshot(
            "text-1",
            new Rect2(20, 5, 40, 15),
            new Point2(30, 10),
            OriginalTextHeight: 5);
        LayoutRegion[] regions =
        [
            new LayoutRegion("frame-1", LayoutRegionKind.ClosedFrame, new Rect2(0, 0, 200, 100)),
            new LayoutRegion("cell-1", LayoutRegionKind.TableCell, new Rect2(10, 0, 50, 20))
        ];

        LayoutRegion? assigned = RegionAssigner.Assign(text, regions);

        AssertEx.Equal("cell-1", assigned?.Id);
    }

    // Break caught: text without a reliable boundary is pulled into a nearby column and moved.
    public static void RegionAssignmentLeavesOutsideTextUnassigned()
    {
        var text = new TextLayoutSnapshot(
            "text-2",
            new Rect2(220, 5, 240, 15),
            new Point2(230, 10),
            OriginalTextHeight: 5);
        LayoutRegion[] regions =
        [
            new LayoutRegion("frame-1", LayoutRegionKind.ClosedFrame, new Rect2(0, 0, 200, 100))
        ];

        AssertEx.Equal<LayoutRegion?>(null, RegionAssigner.Assign(text, regions));
    }

    // Break caught: adjacent note columns are merged even though a full-height separator divides them.
    public static void VerticalSeparatorCreatesIndependentNoteColumns()
    {
        LayoutRegion[] columns = NoteColumnDetector.Detect(
            new Rect2(0, 0, 100, 80),
            [new Segment2(new Point2(50, 0), new Point2(50, 80))],
            [new Point2(10, 70), new Point2(70, 70)]);

        AssertEx.Equal(2, columns.Length);
        AssertEx.Equal(new Rect2(0, 0, 50, 80), columns[0].Bounds);
        AssertEx.Equal(new Rect2(50, 0, 100, 80), columns[1].Bounds);
        AssertEx.True(columns.All(column => column.Kind == LayoutRegionKind.NoteColumn));
    }

    // Break caught: a block definition passes locally but its rotated/scaled instance collides in world space.
    public static void TransformMapsLocalBoundsIntoWorldSpace()
    {
        Transform2 transform = Transform2.FromScaleRotationTranslation(
            scaleX: 2,
            scaleY: 3,
            rotationRadians: Math.PI / 2,
            translationX: 10,
            translationY: 20);

        Rect2 world = transform.Apply(new Rect2(0, 0, 2, 1));

        AssertEx.True(Math.Abs(world.Left - 7) < 1e-9);
        AssertEx.True(Math.Abs(world.Bottom - 20) < 1e-9);
        AssertEx.True(Math.Abs(world.Right - 10) < 1e-9);
        AssertEx.True(Math.Abs(world.Top - 24) < 1e-9);
    }

    // Break caught: overlap already present in the Chinese source is reported as newly introduced.
    public static void LayoutRiskSubtractsSourceOverlapBeforeScoring()
    {
        LayoutRisk risk = LayoutRiskClassifier.Classify(new LayoutRiskInput(
            LayoutRiskCode.TextOverlap,
            SourceOverlapArea: 12,
            CandidateOverlapArea: 16,
            SmallerCandidateArea: 100));

        AssertEx.Equal(LayoutRiskLevel.Low, risk.Level);
        AssertEx.True(Math.Abs(risk.NewOverlapRatio - 0.04) < 1e-9);
    }

    // Break caught: a small numeric overlap hides a semantic cross-cell/cross-column or anchor failure.
    public static void CrossRegionAndAnchorDriftAreAlwaysHighRisk()
    {
        AssertEx.Equal(
            LayoutRiskLevel.High,
            LayoutRiskClassifier.Classify(new LayoutRiskInput(
                LayoutRiskCode.CrossRegion,
                CandidateOverlapArea: 1,
                SmallerCandidateArea: 100)).Level);
        AssertEx.Equal(
            LayoutRiskLevel.High,
            LayoutRiskClassifier.Classify(new LayoutRiskInput(
                LayoutRiskCode.AnchorDrift,
                CandidateOverlapArea: 1,
                SmallerCandidateArea: 100)).Level);
    }

    // Break caught: the soft CAD overlap gate silently reverts to the old single ten-percent threshold.
    public static void NewOverlapUsesSoftFiveAndFifteenPercentThresholds()
    {
        static LayoutRiskLevel Classify(double overlap) =>
            LayoutRiskClassifier.Classify(new LayoutRiskInput(
                LayoutRiskCode.TextOverlap,
                CandidateOverlapArea: overlap,
                SmallerCandidateArea: 100)).Level;

        AssertEx.Equal(LayoutRiskLevel.Low, Classify(4));
        AssertEx.Equal(LayoutRiskLevel.Medium, Classify(10));
        AssertEx.Equal(LayoutRiskLevel.High, Classify(20));
    }

    // Break caught: a nested definition is audited once in local coordinates instead of once per world instance.
    public static void NestedBlockDefinitionIsExpandedForEveryWorldInstance()
    {
        var definitions = new Dictionary<string, BlockDefinitionNode>(StringComparer.Ordinal)
        {
            ["ModelSpace"] = new BlockDefinitionNode("ModelSpace",
            [
                new BlockReferenceNode("A1", "BlockA", Transform2.Translation(0, 0)),
                new BlockReferenceNode("A2", "BlockA", Transform2.Translation(100, 0))
            ]),
            ["BlockA"] = new BlockDefinitionNode("BlockA",
            [
                new BlockReferenceNode("B1", "BlockB", Transform2.Translation(0, 20))
            ]),
            ["BlockB"] = new BlockDefinitionNode("BlockB", [])
        };

        BlockInstancePath[] instances = BlockInstanceExpander
            .Expand("ModelSpace", definitions)
            .Where(instance => instance.DefinitionId == "BlockB")
            .OrderBy(instance => instance.Path, StringComparer.Ordinal)
            .ToArray();

        AssertEx.Equal(2, instances.Length);
        AssertEx.Equal("ModelSpace/BlockA[A1]/BlockB[B1]", instances[0].Path);
        AssertEx.Equal("ModelSpace/BlockA[A2]/BlockB[B1]", instances[1].Path);
        AssertEx.Equal(new Rect2(0, 20, 1, 21), instances[0].WorldTransform.Apply(new Rect2(0, 0, 1, 1)));
        AssertEx.Equal(new Rect2(100, 20, 101, 21), instances[1].WorldTransform.Apply(new Rect2(0, 0, 1, 1)));
    }

    // Break caught: a long drawing-list title is routed by word count into the narrative-column reflow.
    public static void TableCellRegionRoutesLongTextToTableLayout()
    {
        AssertEx.Equal(
            LayoutHandlerKind.TableCell,
            LayoutRouter.Select(
                LayoutRegionKind.TableCell,
                "Main Control Room Door and Window Layout"));
    }

    // Break caught: centered or right-aligned DBText becomes top-left MText and visibly shifts inside its cell.
    public static void TextAlignmentMapsToMatchingMTextAttachment()
    {
        AssertEx.Equal(
            TextAttachmentKind.MiddleCenter,
            TextAnchorPolicy.Map("TextCenter", "TextVerticalMid"));
        AssertEx.Equal(
            TextAttachmentKind.BottomRight,
            TextAnchorPolicy.Map("TextRight", "TextBase"));
        AssertEx.Equal(
            TextAttachmentKind.TopLeft,
            TextAnchorPolicy.Map("TextLeft", "TextTop"));
    }

    // Break caught: DBText-to-MText conversion changes the insertion anchor by a visible amount.
    public static void MTextConversionPreservesVisualAnchorWithinTolerance()
    {
        Point2 sourceAnchor = new(25, 10);
        Point2 candidateAnchor = new(25.4, 10.2);

        AssertEx.True(TextAnchorPolicy.IsPreserved(
            sourceAnchor,
            candidateAnchor,
            originalTextHeight: 4));
        AssertEx.False(TextAnchorPolicy.IsPreserved(
            sourceAnchor,
            new Point2(26, 10),
            originalTextHeight: 4));
    }

    // Break caught: uncertain text is force-fitted into a guessed region and moved.
    public static void UnassignedTextIsPreservedWithoutReflow()
    {
        AssertEx.Equal(
            LayoutHandlerKind.Preserve,
            LayoutRouter.Select(null, "Long but spatially uncertain translated note text"));
        AssertEx.Equal(
            LayoutHandlerKind.Preserve,
            LayoutRouter.Select(LayoutRegionKind.Generic, "Long generic label text"));
    }

    // Break caught: a three-column notes page without drawn separators is collapsed into one reflow group.
    public static void StableTextStartBandsCreateNoteColumnsWithoutSeparatorLines()
    {
        LayoutRegion[] columns = NoteColumnDetector.Detect(
            new Rect2(0, 0, 120, 80),
            [],
            [
                new Point2(10, 70), new Point2(11, 50), new Point2(10, 30),
                new Point2(48, 70), new Point2(49, 50), new Point2(48, 30),
                new Point2(88, 70), new Point2(89, 50), new Point2(88, 30)
            ]);

        AssertEx.Equal(3, columns.Length);
        AssertEx.True(columns[0].Bounds.Right < columns[1].Bounds.Left + 1e-9);
        AssertEx.True(columns[1].Bounds.Right < columns[2].Bounds.Left + 1e-9);
        AssertEx.True(columns[0].Bounds.Contains(new Rect2(10, 30, 11, 70)));
        AssertEx.True(columns[1].Bounds.Contains(new Rect2(48, 30, 49, 70)));
        AssertEx.True(columns[2].Bounds.Contains(new Rect2(88, 30, 89, 70)));
    }

    // Break caught: ordinary CAD contact is treated as a hard failure and suppresses a usable candidate.
    public static void LowAndMediumLayoutRisksDoNotFailHardGate()
    {
        HardGateResult hardGate = LayoutAuditPolicy.EvaluateHardGate(new HardGateInput(
            CjkResidualCount: 0,
            MissingRecordCount: 0,
            CandidateReopened: true,
            NonTextSignatureMatches: true,
            TableGridSignatureMatches: true,
            SourceAndWorkingPreserved: true));
        LayoutRisk[] risks =
        [
            new LayoutRisk(LayoutRiskCode.TextOverlap, LayoutRiskLevel.Low, 0.04),
            new LayoutRisk(LayoutRiskCode.GeometryOverlap, LayoutRiskLevel.Medium, 0.10)
        ];

        AssertEx.True(hardGate.Passed);
        AssertEx.True(LayoutAuditPolicy.CanPublishCandidate(hardGate, risks));
    }

    // Break caught: a translated standard name touching an isolated source
    // comma is scored against the comma's tiny area and becomes a false blocker.
    public static void StandalonePunctuationContactDoesNotBlockLayout()
    {
        AssertEx.False(LayoutTextOverlapPolicy.ShouldReport(
            ",",
            "Indoor Environmental Control Code"));
        AssertEx.False(LayoutTextOverlapPolicy.ShouldReport(
            "Wind resistance per current standard.",
            "，"));
        AssertEx.True(LayoutTextOverlapPolicy.ShouldReport(
            "300N/mm",
            "Design Strength"));
        AssertEx.True(LayoutTextOverlapPolicy.ShouldReport(
            "A-A",
            "Section"));
    }

    // Break caught: font substitution changes the measured extents of two
    // untouched English fragments and falsely blocks an otherwise valid translation.
    public static void UnchangedTextPairDoesNotCreateTranslationOverlapRisk()
    {
        AssertEx.False(LayoutTextOverlapPolicy.ShouldReport(
            "C OF SECONDARY ",
            @"\A1;L",
            leftChanged: false,
            rightChanged: false));
        AssertEx.True(LayoutTextOverlapPolicy.ShouldReport(
            "C OF SECONDARY ",
            @"\A1;L",
            leftChanged: true,
            rightChanged: false));
        AssertEx.True(LayoutTextOverlapPolicy.ShouldReport(
            "C OF SECONDARY ",
            @"\A1;L",
            leftChanged: false,
            rightChanged: true));
    }

    // Break caught: zero layout overlap masks missing text, CJK, reopen, or protected-geometry damage.
    public static void ContentReopenAndStructureFailuresRemainHardFailures()
    {
        HardGateResult result = LayoutAuditPolicy.EvaluateHardGate(new HardGateInput(
            CjkResidualCount: 1,
            MissingRecordCount: 2,
            CandidateReopened: false,
            NonTextSignatureMatches: false,
            TableGridSignatureMatches: false,
            SourceAndWorkingPreserved: false));

        AssertEx.False(result.Passed);
        AssertEx.Contains("cjk_residual", result.ErrorCodes);
        AssertEx.Contains("missing_records", result.ErrorCodes);
        AssertEx.Contains("candidate_reopen_failed", result.ErrorCodes);
        AssertEx.Contains("non_text_mutation", result.ErrorCodes);
        AssertEx.Contains("table_grid_mutation", result.ErrorCodes);
        AssertEx.Contains("source_or_working_mutation", result.ErrorCodes);
    }

    // Break caught: a high-risk region triggers an unbounded AutoCAD retry loop.
    public static void CorrectionPolicyAllowsBoundedReopenedFeedbackPasses()
    {
        LayoutRisk[] high =
        [
            new LayoutRisk(LayoutRiskCode.CrossRegion, LayoutRiskLevel.High, 0.01)
        ];

        AssertEx.True(LayoutCorrectionPolicy.ShouldRunAnotherPass(completedPassIndex: 1, high));
        AssertEx.True(LayoutCorrectionPolicy.ShouldRunAnotherPass(completedPassIndex: 2, high));
        AssertEx.False(LayoutCorrectionPolicy.ShouldRunAnotherPass(
            completedPassIndex: LayoutCorrectionPolicy.MaximumPasses,
            high));
        AssertEx.False(LayoutCorrectionPolicy.ShouldRunAnotherPass(
            completedPassIndex: 1,
            [new LayoutRisk(LayoutRiskCode.TextOverlap, LayoutRiskLevel.Medium, 0.10)]));
    }

    // Break caught: one passing block instance is taken as evidence that all repeated instances passed.
    public static void InstanceCoverageReportsEveryMissingBlockPath()
    {
        string[] missing = LayoutInstanceCoverage.Missing(
            ["Model/A[1]/B[3]", "Model/A[2]/B[3]", "Layout/A[4]/B[3]"],
            ["Model/A[1]/B[3]"]);

        AssertEx.SequenceEqual(
            ["Layout/A[4]/B[3]", "Model/A[2]/B[3]"],
            missing);
    }

    // Break caught: DBText without temporary GeometricExtents is dropped from the topology baseline.
    public static void TextBoundsEstimatorRecoversMissingAutocadExtents()
    {
        Rect2 bounds = TextBoundsEstimator.Estimate(
            new Point2(50, 10),
            textHeight: 2,
            widthFactor: 1,
            text: "结构说明",
            horizontalMode: "TextCenter",
            verticalMode: "TextBase",
            rotationRadians: 0);

        AssertEx.Equal(new Rect2(46, 9.6, 54, 12), bounds);
    }

    // Break caught: an unchanged ATTDEF without reopened GeometricExtents aborts the entire candidate audit.
    public static void LayoutAuditUsesBaselineOnlyForUnmodifiedUnmeasurableText()
    {
        var baseline = new Rect2(10, 20, 30, 40);
        var measured = new Rect2(11, 21, 31, 41);

        AssertEx.Equal(measured, LayoutAuditBounds.Resolve(measured, baseline, wasAdjusted: false));
        AssertEx.Equal(baseline, LayoutAuditBounds.Resolve(null, baseline, wasAdjusted: false));
        AssertEx.Equal<Rect2?>(null, LayoutAuditBounds.Resolve(null, baseline, wasAdjusted: true));
    }

    // Break caught: real multi-column design notes are left unassigned because they have no closed frame.
    public static void DenseLongTextBandsCreateNarrativeColumns()
    {
        NarrativeLayoutSample[] samples = Enumerable.Range(0, 6)
            .SelectMany(row => new[] { 10d, 50d, 90d }.Select(x =>
                new NarrativeLayoutSample(
                    new Point2(x, 100 - row * 10),
                    new Rect2(x, 98 - row * 10, x + 24, 101 - row * 10),
                    "Long professional structural note with enough words for wrapping.",
                    IsLeftAligned: true)))
            .ToArray();

        LayoutRegion[] columns = NarrativeColumnDetector.Detect(samples, medianTextHeight: 3);

        AssertEx.Equal(3, columns.Length);
        AssertEx.True(columns.All(column => column.Kind == LayoutRegionKind.NoteColumn));
    }

    // Break caught: multiple translated texts on the same table row each receive the
    // full parent cell, so their allowed MText boxes completely overlap.
    public static void SideBySideTextsReceiveMutuallyExclusiveAllowedBoxes()
    {
        var parent = new Rect2(0, 0, 100, 20);
        LayoutTextBoxSample[] samples =
        [
            new("left", new Rect2(10, 8, 20, 12), 4),
            new("middle", new Rect2(30, 8, 40, 12), 4),
            new("right", new Rect2(50, 8, 60, 12), 4),
            new("other-row", new Rect2(10, 1, 20, 5), 4)
        ];

        IReadOnlyDictionary<string, Rect2> boxes =
            ExclusiveTextBoxAllocator.Allocate(parent, samples);

        AssertEx.Equal(new Rect2(0, 6.7, 24.8, 20), boxes["left"]);
        AssertEx.Equal(new Rect2(25.2, 0, 44.8, 20), boxes["middle"]);
        AssertEx.Equal(new Rect2(45.2, 0, 100, 20), boxes["right"]);
        AssertEx.Equal(new Rect2(0, 0, 100, 6.3), boxes["other-row"]);
        AssertEx.True(boxes["left"].Right < boxes["middle"].Left);
        AssertEx.True(boxes["middle"].Right < boxes["right"].Left);
        AssertEx.True(boxes["other-row"].Top < boxes["left"].Bottom);
    }

    // Break caught: a short translated label receives only its tiny source width because
    // boundaries are cut between source edges instead of between neighboring centers.
    public static void NarrowLabelReceivesSpaceBetweenNeighborCenters()
    {
        var parent = new Rect2(0, 0, 100, 20);
        LayoutTextBoxSample[] samples =
        [
            new("wide-left", new Rect2(0, 5, 40, 15), 10),
            new("narrow", new Rect2(45, 5, 50, 15), 10),
            new("wide-right", new Rect2(55, 5, 95, 15), 10)
        ];

        IReadOnlyDictionary<string, Rect2> boxes =
            ExclusiveTextBoxAllocator.Allocate(parent, samples);

        AssertEx.True(boxes["narrow"].Width >= 26);
        AssertEx.True(boxes["wide-left"].Right < boxes["narrow"].Left);
        AssertEx.True(boxes["narrow"].Right < boxes["wide-right"].Left);
    }

    // Break caught: a title block with many short fields and two remarks is reflowed as narrative notes.
    public static void ShortTitleBlockLabelsDoNotCreateNarrativeColumns()
    {
        NarrativeLayoutSample[] labels = Enumerable.Range(0, 24)
            .Select(index => new NarrativeLayoutSample(
                new Point2((index % 4) * 20, 30 - (index / 4) * 4),
                new Rect2((index % 4) * 20, 29 - (index / 4) * 4, (index % 4) * 20 + 8, 31 - (index / 4) * 4),
                index < 2
                    ? "A longer title-block remark that must remain anchored."
                    : "Short label",
                IsLeftAligned: true))
            .ToArray();

        AssertEx.Equal(0, NarrativeColumnDetector.Detect(labels, medianTextHeight: 2).Length);
    }

    // Break caught: a parent block spanning several sheets is mistaken for one giant notes page.
    public static void WidelyScatteredLongTextDoesNotCreateNarrativeColumns()
    {
        NarrativeLayoutSample[] samples = Enumerable.Range(0, 6)
            .SelectMany(row => new[] { 0d, 400d, 800d }.Select(x =>
                new NarrativeLayoutSample(
                    new Point2(x, 400 - row * 40),
                    new Rect2(x, 398 - row * 40, x + 100, 401 - row * 40),
                    "Long professional note located in one of several separate sheets.",
                    IsLeftAligned: true)))
            .ToArray();

        AssertEx.Equal(0, NarrativeColumnDetector.Detect(samples, medianTextHeight: 3).Length);
    }

    // Break caught: verified DBText-to-MText layout conversions fail content identity checks.
    public static void CandidateContentAcceptsOnlyExplicitDbTextToMTextLayoutIdentity()
    {
        (ManifestRecord manifest, TranslationRecord translation) = CandidateFixture();
        var converted = new CandidateTextRecord("candidate-1", "2B", "AcDbMText", "contents", "Valve \\C1;");
        var identity = new Dictionary<string, CandidateIdentityOverride>(StringComparer.Ordinal)
        {
            ["candidate-1"] = new CandidateIdentityOverride("2B", "AcDbMText", "contents")
        };

        AssertEx.False(CandidateContentVerifier.Verify([manifest], [translation], [converted]).IsValid);
        AssertEx.True(CandidateContentVerifier.Verify([manifest], [translation], [converted], identity).IsValid);
        AssertEx.False(CandidateContentVerifier.Verify(
            [manifest],
            [translation],
            [converted with { ObjectType = "AcDbAttribute" }],
            identity).IsValid);
    }

    // Break caught: layout width compression is reported as translated-content corruption.
    public static void GeneratedWidthWrapperIsRemovedWithoutStrippingSourceFormatting()
    {
        AssertEx.Equal(
            @"{\C1;Valve}",
            LayoutTextNormalization.RemoveGeneratedWidthWrapper(@"{\W0.7;{\C1;Valve}}"));
        AssertEx.Equal(
            @"{\C1;Valve}",
            LayoutTextNormalization.RemoveGeneratedWidthWrapper(@"{\C1;Valve}"));
        AssertEx.Equal(
            @"{\W0.5;Valve}",
            LayoutTextNormalization.RemoveGeneratedWidthWrapper(@"{\W0.5;Valve}"));
    }

    // Break caught: unresolved high layout risks are reported but the temporary
    // drawing is still promoted to the publishable candidate path.
    public static void HighLayoutRiskBlocksCandidatePublication()
    {
        HardGateResult hardGate = LayoutAuditPolicy.EvaluateHardGate(new HardGateInput(
            CjkResidualCount: 0,
            MissingRecordCount: 0,
            CandidateReopened: true,
            NonTextSignatureMatches: true,
            TableGridSignatureMatches: true,
            SourceAndWorkingPreserved: true));
        LayoutRisk[] risks =
        [
            new(LayoutRiskCode.TextOverlap, LayoutRiskLevel.High, 0.25)
        ];

        AssertEx.False(LayoutAuditPolicy.CanPublishCandidate(hardGate, risks));
    }

    // Break caught: overlap audit compares only texts sharing one non-empty region,
    // so the majority of translated texts are invisible to the collision check.
    public static void OverlapAuditIncludesUnassignedAndCrossRegionPairs()
    {
        LayoutOverlapItem[] items =
        [
            new("A", "definition-1", ""),
            new("B", "definition-1", "note-column-2"),
            new("C", "definition-1", "table-cell-3"),
            new("D", "definition-2", "")
        ];

        string[] pairs = LayoutOverlapPairSelector.Select(items)
            .Select(pair => $"{pair.LeftRecordId}-{pair.RightRecordId}")
            .ToArray();

        AssertEx.SequenceEqual(["A-B", "A-C", "B-C"], pairs);
    }

    // Break caught: multiple borderless note blocks in one definition remain
    // unassigned because narrative detection expects one large 12-row page.
    public static void BorderlessNoteBlocksReceiveSeparateOccupancyRegions()
    {
        NarrativeLayoutSample[] samples =
        [
            .. Enumerable.Range(0, 4).Select(row => new NarrativeLayoutSample(
                new Point2(0, 100 - row * 5),
                new Rect2(0, 98 - row * 5, 34, 101 - row * 5),
                $"Foundation note row {row + 1} with professional translated content.",
                IsLeftAligned: true)),
            .. Enumerable.Range(0, 4).Select(row => new NarrativeLayoutSample(
                new Point2(60, 100 - row * 5),
                new Rect2(60, 98 - row * 5, 94, 101 - row * 5),
                $"Ground treatment row {row + 1} with professional translated content.",
                IsLeftAligned: true))
        ];

        LayoutRegion[] regions = NarrativeOccupancyDetector.Detect(
            samples,
            medianTextHeight: 3);

        AssertEx.Equal(2, regions.Length);
        AssertEx.True(regions.All(region => region.Kind == LayoutRegionKind.NoteColumn));
        AssertEx.True(regions[0].Bounds.Right < regions[1].Bounds.Left);
    }

    // Break caught: the short heading of a real note block is excluded by the
    // per-record 40-character rule and later collides with the reflowed body.
    public static void ShortNoteHeadingIsKeptWithNumberedRows()
    {
        NarrativeLayoutSample[] samples =
        [
            new(new Point2(10, 50), new Rect2(10, 48, 20, 51), "Notes:", true),
            new(new Point2(10, 45), new Rect2(10, 43, 45, 46), "1. First translated construction requirement.", true),
            new(new Point2(10, 40), new Rect2(10, 38, 45, 41), "2. Second translated construction requirement.", true),
            new(new Point2(10, 35), new Rect2(10, 33, 45, 36), "3. Third translated construction requirement.", true)
        ];

        LayoutRegion[] regions = NarrativeOccupancyDetector.Detect(
            samples,
            medianTextHeight: 3);

        AssertEx.Equal(1, regions.Length);
        AssertEx.True(regions[0].Bounds.Contains(samples[0].Bounds));
    }

    // Break caught: changed text without a table or note-column handler silently
    // disappears into the misleading "unchanged" count.
    public static void LayoutCoverageReportsEveryUnhandledChangedRecord()
    {
        string[] uncovered = LayoutCoveragePolicy.FindUncovered(
            ["changed-a", "changed-b", "changed-c"],
            ["changed-a", "changed-c"]);

        AssertEx.SequenceEqual(["changed-b"], uncovered);
    }

    // Break caught: assigning note regions by geometric containment pulls unrelated
    // short labels and existing text into the reflow group.
    public static void NarrativeOccupancyReturnsExplicitGroupMembership()
    {
        NarrativeOccupancySample[] samples =
        [
            new("note-1", new Point2(0, 30), new Rect2(0, 28, 35, 31), "1. First translated note requirement.", true),
            new("note-2", new Point2(0, 25), new Rect2(0, 23, 35, 26), "2. Second translated note requirement.", true),
            new("note-3", new Point2(0, 20), new Rect2(0, 18, 35, 21), "3. Third translated note requirement.", true),
            new("label", new Point2(70, 25), new Rect2(70, 23, 80, 26), "Section", true)
        ];

        NarrativeOccupancyGroup[] groups = NarrativeOccupancyDetector.DetectGroups(
            samples,
            medianTextHeight: 3);

        AssertEx.Equal(1, groups.Length);
        AssertEx.SequenceEqual(["note-1", "note-2", "note-3"], groups[0].MemberIds.OrderBy(id => id));
    }

    // Break caught: only translated rows are reflowed while unchanged numeric or
    // already-English rows in the same source paragraph remain in the old positions.
    public static void NarrativeReflowIncludesUnchangedMembersOfChangedGroup()
    {
        LayoutParticipationItem[] items =
        [
            new("translated-row", "note-1", IsChanged: true),
            new("existing-row", "note-1", IsChanged: false),
            new("other-label", "label-2", IsChanged: false)
        ];

        string[] participants = LayoutGroupParticipation.SelectNarrativeParticipants(items);

        AssertEx.SequenceEqual(["existing-row", "translated-row"], participants.OrderBy(id => id));
    }

    // Break caught: the exact Chinese source union was treated as the English
    // text box, leaving no legal whitespace for longer translated lines.
    public static void NarrativeEnvelopeExpandsIntoSourceWhitespace()
    {
        NarrativeOccupancySample[] samples =
        [
            new("n1", new Point2(10, 40), new Rect2(10, 38, 40, 41), "1. First translated note.", true),
            new("n2", new Point2(10, 35), new Rect2(10, 33, 40, 36), "2. Second translated note.", true),
            new("n3", new Point2(10, 30), new Rect2(10, 28, 40, 31), "3. Third translated note.", true)
        ];
        NarrativeOccupancyGroup group = new(
            new LayoutRegion("note", LayoutRegionKind.NoteColumn, new Rect2(10, 28, 40, 41)),
            ["n1", "n2", "n3"]);

        NarrativeOccupancyGroup expanded = NarrativeOccupancyEnvelope.Expand(
            [group],
            samples,
            [new Rect2(0, 0, 100, 60)],
            medianTextHeight: 3).Single();

        AssertEx.True(expanded.Region.Bounds.Right > 40);
        AssertEx.True(expanded.Region.Bounds.Bottom < 28);
        AssertEx.True(new Rect2(0, 0, 100, 60).Contains(expanded.Region.Bounds));
    }

    // Break caught: giving every paragraph the whole containing frame makes
    // adjacent English text boxes overlap even before their text is laid out.
    public static void NeighboringNarrativeEnvelopesRemainMutuallyExclusive()
    {
        NarrativeOccupancySample[] samples =
        [
            new("l1", new Point2(10, 40), new Rect2(10, 38, 35, 41), "1. Left translated note.", true),
            new("l2", new Point2(10, 35), new Rect2(10, 33, 35, 36), "2. Left translated note.", true),
            new("l3", new Point2(10, 30), new Rect2(10, 28, 35, 31), "3. Left translated note.", true),
            new("r1", new Point2(60, 40), new Rect2(60, 38, 85, 41), "1. Right translated note.", true),
            new("r2", new Point2(60, 35), new Rect2(60, 33, 85, 36), "2. Right translated note.", true),
            new("r3", new Point2(60, 30), new Rect2(60, 28, 85, 31), "3. Right translated note.", true)
        ];
        NarrativeOccupancyGroup[] groups =
        [
            new(new LayoutRegion("left", LayoutRegionKind.NoteColumn, new Rect2(10, 28, 35, 41)), ["l1", "l2", "l3"]),
            new(new LayoutRegion("right", LayoutRegionKind.NoteColumn, new Rect2(60, 28, 85, 41)), ["r1", "r2", "r3"])
        ];

        NarrativeOccupancyGroup[] expanded = NarrativeOccupancyEnvelope.Expand(
            groups,
            samples,
            [new Rect2(0, 0, 100, 60)],
            medianTextHeight: 3);

        AssertEx.True(expanded[0].Region.Bounds.Right < expanded[1].Region.Bounds.Left);
        AssertEx.True(expanded[0].Region.Bounds.Right > 35);
        AssertEx.True(expanded[1].Region.Bounds.Left < 60);
    }

    // Break caught: expanding to a sheet frame without considering source text
    // steals the title block or the next independent note block.
    public static void NarrativeEnvelopeStopsBeforeUnrelatedSourceText()
    {
        NarrativeOccupancySample[] samples =
        [
            new("n1", new Point2(10, 40), new Rect2(10, 38, 40, 41), "1. First translated note.", true),
            new("n2", new Point2(10, 35), new Rect2(10, 33, 40, 36), "2. Second translated note.", true),
            new("n3", new Point2(10, 30), new Rect2(10, 28, 40, 31), "3. Third translated note.", true),
            new("title", new Point2(10, 10), new Rect2(10, 8, 50, 12), "Drawing title", true)
        ];
        NarrativeOccupancyGroup group = new(
            new LayoutRegion("note", LayoutRegionKind.NoteColumn, new Rect2(10, 28, 40, 41)),
            ["n1", "n2", "n3"]);

        NarrativeOccupancyGroup expanded = NarrativeOccupancyEnvelope.Expand(
            [group],
            samples,
            [new Rect2(0, 0, 100, 60)],
            medianTextHeight: 3).Single();

        AssertEx.True(expanded.Region.Bounds.Bottom > 12);
    }

    // Break caught: A is close to B and B is close to C, so a pairwise graph
    // incorrectly merges three independent columns even though A and C are far apart.
    public static void TransitiveStartBandsDoNotMergeDistantNoteBlocks()
    {
        NarrativeOccupancySample[] samples = Enumerable.Range(0, 3)
            .SelectMany(column => Enumerable.Range(0, 3).Select(row =>
            {
                double x = column * 15;
                double y = 50 - row * 5;
                return new NarrativeOccupancySample(
                    $"{column}-{row}",
                    new Point2(x, y),
                    new Rect2(x, y - 2, x + 12, y + 1),
                    $"{row + 1}. Independent translated note in column {column + 1}.",
                    true);
            }))
            .ToArray();

        NarrativeOccupancyGroup[] groups = NarrativeOccupancyDetector.DetectGroups(
            samples,
            medianTextHeight: 3);

        AssertEx.Equal(3, groups.Length);
        AssertEx.True(groups.All(group => group.MemberIds.Count == 3));
    }

    // Break caught: one real three-column notes panel contains several indentation
    // starts per column; treating every indent as a separate box creates overlaps.
    public static void DenseIndentedNoteRowsMergeIntoMacroColumns()
    {
        double[] macroStarts = [0, 60, 120];
        double[] indents = [0, 15, 30];
        NarrativeOccupancySample[] samples = macroStarts
            .SelectMany((macro, column) => indents.SelectMany((indent, section) =>
                Enumerable.Range(0, 3).Select(row =>
                {
                    double x = macro + indent;
                    double y = 100 - section * 25 - row * 5;
                    return new NarrativeOccupancySample(
                        $"{column}-{section}-{row}",
                        new Point2(x, y),
                        new Rect2(x, y - 2, x + 12, y + 1),
                        $"{row + 1}. Dense translated design note in macro column {column + 1}.",
                        true);
                })))
            .ToArray();

        NarrativeOccupancyGroup[] groups = NarrativeOccupancyDetector.DetectGroups(
            samples,
            medianTextHeight: 3);

        AssertEx.Equal(3, groups.Length);
        AssertEx.True(groups.All(group => group.MemberIds.Count == 9));
    }

    // Break caught: sparse inline fragments form a transitive chain between
    // dense column starts, causing several independent note columns to be
    // reflowed as one huge column and moved far away from their source anchors.
    public static void FragmentBridgesDoNotMergeNeighboringMacroColumns()
    {
        double[] macroStarts = [0, 100, 200];
        var samples = new List<NarrativeOccupancySample>();
        for (int column = 0; column < macroStarts.Length; column++)
        {
            double start = macroStarts[column];
            samples.AddRange(Enumerable.Range(0, 12).Select(row =>
            {
                double y = 150 - row * 5;
                return new NarrativeOccupancySample(
                    $"base-{column}-{row}",
                    new Point2(start, y),
                    new Rect2(start, y - 2, start + 12, y + 1),
                    $"{row + 1}. Dense translated note in macro column {column + 1}.",
                    true);
            }));

            if (column + 1 == macroStarts.Length)
            {
                continue;
            }

            for (int offset = 15; offset <= 90; offset += 15)
            {
                samples.AddRange(Enumerable.Range(0, 3).Select(row =>
                {
                    double y = 120 - row * 5;
                    return new NarrativeOccupancySample(
                        $"fragment-{column}-{offset}-{row}",
                        new Point2(start + offset, y),
                        new Rect2(start + offset, y - 2, start + offset + 8, y + 1),
                        "Continuation fragment that belongs to the preceding source column.",
                        true);
                }));
            }
        }

        NarrativeOccupancyGroup[] groups = NarrativeOccupancyDetector.DetectGroups(
            samples,
            medianTextHeight: 3);

        AssertEx.Equal(3, groups.Length);
        for (int column = 0; column < macroStarts.Length; column++)
        {
            string expected = $"base-{column}-0";
            AssertEx.True(groups.Count(group => group.MemberIds.Contains(expected)) == 1);
        }
    }

    // Break caught: two independent three-column note panels reuse the same
    // horizontal starts; merging by X alone creates one enormous region that
    // moves text across drawing frames.
    public static void DistantNotePanelsArePartitionedBeforeMacroColumnMerge()
    {
        double[] macroStarts = [0, 100, 200];
        double[] panelTops = [150, -500];
        var samples = new List<NarrativeOccupancySample>();
        for (int panel = 0; panel < panelTops.Length; panel++)
        {
            for (int column = 0; column < macroStarts.Length; column++)
            {
                double start = macroStarts[column];
                samples.AddRange(Enumerable.Range(0, 12).Select(row =>
                {
                    double y = panelTops[panel] - row * 5;
                    return new NarrativeOccupancySample(
                        $"panel-{panel}-base-{column}-{row}",
                        new Point2(start, y),
                        new Rect2(start, y - 2, start + 12, y + 1),
                        $"{row + 1}. Independent translated note panel {panel + 1}, column {column + 1}.",
                        true);
                }));

                if (column + 1 == macroStarts.Length)
                {
                    continue;
                }

                for (int offset = 15; offset <= 90; offset += 15)
                {
                    samples.AddRange(Enumerable.Range(0, 3).Select(row =>
                    {
                        double y = panelTops[panel] - 30 - row * 5;
                        return new NarrativeOccupancySample(
                            $"panel-{panel}-fragment-{column}-{offset}-{row}",
                            new Point2(start + offset, y),
                            new Rect2(start + offset, y - 2, start + offset + 8, y + 1),
                            "Inline continuation fragment in its own note panel.",
                            true);
                    }));
                }
            }
        }

        NarrativeOccupancyGroup[] groups = NarrativeOccupancyDetector.DetectGroups(
            samples,
            medianTextHeight: 3);

        AssertEx.Equal(6, groups.Length);
        for (int panel = 0; panel < panelTops.Length; panel++)
        {
            for (int column = 0; column < macroStarts.Length; column++)
            {
                string expected = $"panel-{panel}-base-{column}-0";
                AssertEx.True(groups.Count(group => group.MemberIds.Contains(expected)) == 1);
            }
        }
    }

    // Break caught: source extents from long Chinese rows cross the neighboring
    // start column; forcing the region to contain those extents recreates overlap.
    public static void OverlongSourceBoundsDoNotDefeatAnchorPartition()
    {
        NarrativeOccupancySample[] samples =
        [
            new("l1", new Point2(10, 40), new Rect2(10, 38, 70, 41), "1. Left note.", true),
            new("l2", new Point2(10, 35), new Rect2(10, 33, 70, 36), "2. Left note.", true),
            new("l3", new Point2(10, 30), new Rect2(10, 28, 70, 31), "3. Left note.", true),
            new("r1", new Point2(60, 40), new Rect2(60, 38, 95, 41), "1. Right note.", true),
            new("r2", new Point2(60, 35), new Rect2(60, 33, 95, 36), "2. Right note.", true),
            new("r3", new Point2(60, 30), new Rect2(60, 28, 95, 31), "3. Right note.", true)
        ];
        NarrativeOccupancyGroup[] groups =
        [
            new(new LayoutRegion("left", LayoutRegionKind.NoteColumn, new Rect2(10, 28, 70, 41)), ["l1", "l2", "l3"]),
            new(new LayoutRegion("right", LayoutRegionKind.NoteColumn, new Rect2(60, 28, 95, 41)), ["r1", "r2", "r3"])
        ];

        NarrativeOccupancyGroup[] expanded = NarrativeOccupancyEnvelope.Expand(
            groups,
            samples,
            [new Rect2(0, 0, 100, 60)],
            medianTextHeight: 3);

        AssertEx.True(expanded[0].Region.Bounds.Right < expanded[1].Region.Bounds.Left);
        AssertEx.True(expanded[0].Region.Bounds.Right > 10);
        AssertEx.True(expanded[1].Region.Bounds.Left < 60);
    }

    // Break caught: long rows identify the three note columns, but short headings
    // left outside those groups remain at old coordinates and collide after reflow.
    public static void DenseMacroColumnsIncludeShortRowsBetweenNoteSeeds()
    {
        double[] macroStarts = [0, 60, 120];
        double[] indents = [0, 15, 30];
        NarrativeOccupancySample[] longRows = macroStarts
            .SelectMany((macro, column) => indents.SelectMany((indent, section) =>
                Enumerable.Range(0, 3).Select(row =>
                {
                    double x = macro + indent;
                    double y = 100 - section * 25 - row * 5;
                    return new NarrativeOccupancySample(
                        $"{column}-{section}-{row}",
                        new Point2(x, y),
                        new Rect2(x, y - 2, x + 12, y + 1),
                        $"{row + 1}. Dense translated note seed.",
                        true);
                })))
            .ToArray();
        NarrativeOccupancySample[] samples =
        [
            .. longRows,
            .. macroStarts.Select((x, column) => new NarrativeOccupancySample(
                $"short-{column}",
                new Point2(x + 43, 82),
                new Rect2(x + 43, 80, x + 49, 83),
                "Note:",
                true))
        ];

        NarrativeOccupancyGroup[] groups = NarrativeOccupancyDetector.DetectGroups(
            samples,
            medianTextHeight: 3);

        AssertEx.Equal(3, groups.Length);
        AssertEx.True(groups.All(group => group.MemberIds.Count == 10));
        AssertEx.True(groups.SelectMany(group => group.MemberIds)
            .Count(id => id.StartsWith("short-", StringComparison.Ordinal)) == 3);
    }

    // Break caught: a long schedule heading far beyond the final narrative
    // column is absorbed merely because it looks like prose and shares Y range.
    public static void DenseMacroColumnsExcludeDistantLongScheduleLabels()
    {
        double[] macroStarts = [0, 100, 200];
        double[] indents = [0, 15, 30];
        NarrativeOccupancySample[] noteRows = macroStarts
            .SelectMany((macro, column) => indents.SelectMany((indent, section) =>
                Enumerable.Range(0, 3).Select(row =>
                {
                    double x = macro + indent;
                    double y = 100 - section * 25 - row * 5;
                    return new NarrativeOccupancySample(
                        $"{column}-{section}-{row}",
                        new Point2(x, y),
                        new Rect2(x, y - 2, x + 12, y + 1),
                        $"{row + 1}. Dense translated design note.",
                        true);
                })))
            .ToArray();
        NarrativeOccupancySample[] samples =
        [
            .. noteRows,
            new(
                "schedule-heading",
                new Point2(1000, 82),
                new Rect2(1000, 80, 1040, 83),
                "Indoor Construction Method",
                true)
        ];

        NarrativeOccupancyGroup[] groups = NarrativeOccupancyDetector.DetectGroups(
            samples,
            medianTextHeight: 3);

        AssertEx.Equal(3, groups.Length);
        AssertEx.False(groups.SelectMany(group => group.MemberIds)
            .Contains("schedule-heading", StringComparer.Ordinal));
    }

    // Break caught: macro-column consolidation included every left-aligned
    // entity in the enclosing rectangle, so elevations and drawing labels were
    // reflowed as narrative notes and pulled into dense English paragraphs.
    public static void DenseMacroColumnsExcludeDistantDrawingLabels()
    {
        double[] macroStarts = [0, 100, 200];
        double[] indents = [0, 15, 30];
        NarrativeOccupancySample[] noteRows = macroStarts
            .SelectMany((macro, column) => indents.SelectMany((indent, section) =>
                Enumerable.Range(0, 3).Select(row =>
                {
                    double x = macro + indent;
                    double y = 100 - section * 25 - row * 5;
                    return new NarrativeOccupancySample(
                        $"{column}-{section}-{row}",
                        new Point2(x, y),
                        new Rect2(x, y - 2, x + 12, y + 1),
                        $"{row + 1}. Dense translated design note.",
                        true);
                })))
            .ToArray();
        NarrativeOccupancySample[] samples =
        [
            .. noteRows,
            new("elevation", new Point2(-60, 100), new Rect2(-60, 98, -48, 102), "-1.500", true)
        ];

        NarrativeOccupancyGroup[] groups = NarrativeOccupancyDetector.DetectGroups(
            samples,
            medianTextHeight: 3);

        AssertEx.Equal(3, groups.Length);
        AssertEx.False(groups.SelectMany(group => group.MemberIds)
            .Contains("elevation", StringComparer.Ordinal));
    }

    public static void DenseMacroColumnsIncludeShortVerticalContinuations()
    {
        double[] macroStarts = [0, 100, 200];
        double[] indents = [0, 15, 30];
        NarrativeOccupancySample[] noteRows = macroStarts
            .SelectMany((macro, column) => indents.SelectMany((indent, section) =>
                Enumerable.Range(0, 3).Select(row =>
                {
                    double x = macro + indent;
                    double y = 100 - section * 25 - row * 5;
                    return new NarrativeOccupancySample(
                        $"{column}-{section}-{row}",
                        new Point2(x, y),
                        new Rect2(x, y - 2, x + 45, y + 1),
                        $"{row + 1}. Dense translated design note.",
                        true);
                })))
            .ToArray();
        NarrativeOccupancySample[] samples =
        [
            .. noteRows,
            new("short-citation", new Point2(255, 82), new Rect2(255, 80, 265, 83), "(GB edition).", true),
            new("spaced-suffix", new Point2(275, 75), new Rect2(275, 73, 285, 76), "requirements.", true)
        ];

        NarrativeOccupancyGroup[] groups = NarrativeOccupancyDetector.DetectGroups(
            samples,
            medianTextHeight: 3);

        AssertEx.Equal(3, groups.Length);
        AssertEx.True(groups.SelectMany(group => group.MemberIds)
            .Contains("short-citation", StringComparer.Ordinal));
        AssertEx.True(groups.SelectMany(group => group.MemberIds)
            .Contains("spaced-suffix", StringComparer.Ordinal));
    }

    // Break caught: a reinforcement symbol such as %%1306@600X600 contains
    // one Latin X, so it was mistaken for prose and moved several metres.
    // Break caught: multiple source fragments on one visual row were counted as
    // independent vertical rows, multiplying required column height.
    public static void NarrativeRowPlannerKeepsSameHeightFragmentsOnOneRow()
    {
        NarrativeRowItem[] items =
        [
            new("top-left", new Rect2(0, 98, 20, 101)),
            new("top-right", new Rect2(30, 98.2, 50, 101.2)),
            new("second", new Rect2(0, 88, 50, 91))
        ];

        NarrativeLogicalRow[] rows = NarrativeRowPlanner.Group(
            items,
            medianTextHeight: 3);

        AssertEx.Equal(2, rows.Length);
        AssertEx.SequenceEqual(["top-left", "top-right"], rows[0].MemberIds);
        AssertEx.SequenceEqual(["second"], rows[1].MemberIds);
    }

    public static void NarrativeHorizontalPartitionerSplitsDistantSheetPanels()
    {
        NarrativeRowItem[][] panels = NarrativeHorizontalPanelPartitioner.Partition(
            [
                new("left-a", new Rect2(0, 90, 20, 100)),
                new("left-b", new Rect2(80, 70, 100, 80)),
                new("right-a", new Rect2(500, 90, 520, 100)),
                new("right-b", new Rect2(560, 70, 580, 80))
            ],
            medianTextHeight: 10);

        AssertEx.Equal(2, panels.Length);
        AssertEx.SequenceEqual(["left-a", "left-b"], panels[0].Select(item => item.Id));
        AssertEx.SequenceEqual(["right-a", "right-b"], panels[1].Select(item => item.Id));
    }

    public static void NarrativeRightBoundaryIgnoresVerticallyDisjointRegion()
    {
        double right = NarrativeRegionRightBoundary.Resolve(
            left: 100,
            bottom: 0,
            top: 100,
            medianTextHeight: 10,
            sourceRights: [280, 300, 320],
            followingRegions:
            [
                new NarrativeRegionEnvelope(220, -300, -200),
                new NarrativeRegionEnvelope(400, 20, 80)
            ]);

        AssertEx.Equal(320d, right);
    }

    // Break caught: two independent text clusters happen to share a visual
    // baseline. Packing every fragment from the first X position destroys the
    // source layout and can move the right cluster across half the note column.
    public static void NarrativeRowClustersPreserveLargeSourceGaps()
    {
        NarrativeRowItem[] items =
        [
            new("left-label", new Rect2(0, 98, 12, 102)),
            new("right-a", new Rect2(70, 98, 82, 102)),
            new("right-b", new Rect2(83, 98, 96, 102))
        ];

        NarrativeRowCluster[] clusters = NarrativeRowClusterPlanner.Partition(
            items,
            medianTextHeight: 4);

        AssertEx.Equal(2, clusters.Length);
        AssertEx.SequenceEqual(["left-label"], clusters[0].MemberIds);
        AssertEx.SequenceEqual(["right-a", "right-b"], clusters[1].MemberIds);
        AssertEx.Equal(70d, clusters[1].SourceBounds.Left);
    }

    public static void NarrativeRowBoxesFallbackWhenSourceCentersAreOutside()
    {
        NarrativeRowItem[] items =
        [
            new("a", new Rect2(120, 8, 130, 12)),
            new("b", new Rect2(140, 8, 150, 12)),
            new("c", new Rect2(160, 8, 170, 12))
        ];

        IReadOnlyDictionary<string, Rect2> boxes = NarrativeRowBoxAllocator.Allocate(
            new Rect2(0, 0, 100, 20),
            items,
            gutter: 1);

        AssertEx.True(boxes.Values.All(box => box.Width > 0));
        AssertEx.True(boxes["a"].Right < boxes["b"].Left);
        AssertEx.True(boxes["b"].Right < boxes["c"].Left);
    }

    public static void NarrativeInlineFragmentsWrapAsOneVisualRow()
    {
        NarrativeInlinePacking packing = NarrativeInlinePacker.Pack(
            [
                new NarrativeInlineItem("a", 30, 5),
                new NarrativeInlineItem("b", 40, 5),
                new NarrativeInlineItem("c", 50, 5)
            ],
            availableWidth: 100,
            horizontalGap: 2,
            verticalGap: 1);

        AssertEx.Equal(2, packing.LineCount);
        AssertEx.Equal(11d, packing.Height);
        AssertEx.Equal(0d, packing.Placements[0].YOffset);
        AssertEx.Equal(0d, packing.Placements[1].YOffset);
        AssertEx.Equal(6d, packing.Placements[2].YOffset);
    }

    public static void SourceNeighborSlotsAreMutuallyExclusiveOnSameRow()
    {
        LayoutTextBoxSample[] items =
        [
            new("left", new Rect2(10, 8, 25, 12), 4),
            new("middle", new Rect2(40, 8, 50, 12), 4),
            new("right", new Rect2(70, 8, 85, 12), 4)
        ];

        IReadOnlyDictionary<string, Rect2> slots =
            SourceNeighborSlotAllocator.Allocate(items);

        AssertEx.True(slots["left"].Right < slots["middle"].Left);
        AssertEx.True(slots["middle"].Right < slots["right"].Left);
        AssertEx.True(slots["middle"].Contains(items[1].SourceBounds));
    }

    public static void FixedLabelSlotUsesFreeSpaceUntilNeighborMidpoint()
    {
        LayoutTextBoxSample[] items =
        [
            new("gypsum", new Rect2(10, 8, 23, 18), 10),
            new("next-label", new Rect2(100, 8, 113, 18), 10)
        ];

        IReadOnlyDictionary<string, Rect2> slots = SourceNeighborSlotAllocator.Allocate(items);

        AssertEx.True(slots["gypsum"].Width >= 45);
        AssertEx.True(slots["gypsum"].Right < slots["next-label"].Left);
    }

    public static void IsolatedFixedLabelSlotStaysCloseToSourceVisualWidth()
    {
        var source = new Rect2(100, 20, 300, 40);
        IReadOnlyDictionary<string, Rect2> slots =
            SourceNeighborSlotAllocator.Allocate(
                [new LayoutTextBoxSample("isolated", source, 20)]);

        Rect2 slot = slots["isolated"];
        AssertEx.True(slot.Contains(source));
        AssertEx.True(slot.Width <= source.Width * 1.10 + 1e-6);
        AssertEx.True(slot.Height <= source.Height * 1.10 + 1e-6);
    }

    public static void IsolatedFixedLabelSlotAllowsSourceHeightEnglishLabel()
    {
        var source = new Rect2(100, 20, 113, 30);
        IReadOnlyDictionary<string, Rect2> slots =
            SourceNeighborSlotAllocator.Allocate(
                [new LayoutTextBoxSample("gypsum", source, 10)]);

        Rect2 slot = slots["gypsum"];
        AssertEx.True(slot.Contains(source));
        AssertEx.True(slot.Width >= 45);
    }

    public static void FixedLabelMovesInsideBeforeReducingTextHeight()
    {
        var allowed = new Rect2(0, 0, 50, 10);
        var shifted = new Rect2(-5, 0, 45, 10);

        FixedLabelMoveDecision decision =
            FixedLabelContainmentPolicy.Decide(allowed, shifted);

        AssertEx.True(decision.FitsAfterMove);
        AssertEx.Equal(5d, decision.DeltaX);
        AssertEx.Equal(0d, decision.DeltaY);
        AssertEx.True(allowed.Contains(new Rect2(
            shifted.Left + decision.DeltaX,
            shifted.Bottom + decision.DeltaY,
            shifted.Right + decision.DeltaX,
            shifted.Top + decision.DeltaY)));

        AssertEx.False(FixedLabelContainmentPolicy.Decide(
            allowed,
            new Rect2(0, 0, 51, 10)).FitsAfterMove);
    }

    public static void FixedLabelPaddingNeverExcludesItsSourceText()
    {
        var allowed = new Rect2(0, 0, 50, 10);
        var source = new Rect2(0, 0, 13, 10);

        Rect2 selected = FixedLabelPaddingPolicy.Select(
            allowed,
            source,
            proposedInset: 0.5);

        AssertEx.Equal(allowed, selected);
        AssertEx.True(selected.Contains(source));
    }

    public static void LargeInnerFrameBlockIsATextContainer()
    {
        AssertEx.True(SheetFrameCandidatePolicy.IsCandidate(
            "P_INNER_FRAME",
            new Rect2(-105.9, -671.3, 181.1, -471.3),
            medianTextHeight: 5,
            containedTextCount: 30));
    }

    public static void SheetFrameUsesInnerPrintBoundary()
    {
        var outerBlockExtents = new Rect2(-297, -210, 297, 210);
        var innerRedFrame = new Rect2(-272, -200, 287, 200);

        AssertEx.Equal(
            innerRedFrame,
            SheetFrameBoundaryPolicy.Select(
                outerBlockExtents,
                [innerRedFrame]));
    }

    public static void LongCenteredFixedLabelWrapsBeforeEmergencyCompression()
    {
        AssertEx.True(
            FixedLabelTextClassifier.ShouldWrap(
                new LayoutTextProfile(
                    "AcDbText",
                    "TextMid",
                    "See Standard Drawing pages for raft edge detailing and reinforcement.")));
        AssertEx.False(
            FixedLabelTextClassifier.ShouldWrap(
                new LayoutTextProfile("AcDbText", "TextLeft", "Foundation Layout")));
    }

    public static void MTextFormatCodesDoNotTurnShortDrawingLabelsIntoNarrativeNotes()
    {
        NarrativeOccupancySample[] labels =
        [
            new("shell", new Point2(10, 30), new Rect2(10, 28, 25, 32),
                @"{\fFangSong|b0|i0|c134|p49;窑 壳}", true),
            new("diameter", new Point2(10, 20), new Rect2(10, 18, 25, 22),
                @"{\fFangSong|b0|i0|c134|p49;砌筑内径}", true),
            new("parts", new Point2(10, 10), new Rect2(10, 8, 25, 12),
                @"{\fFangSong|b0|i0|c134|p49;16等份均分}", true)
        ];

        AssertEx.Equal(
            0,
            NarrativeOccupancyDetector.DetectGroups(labels, medianTextHeight: 4).Length);
    }

    public static void NarrativeRegionsAreClassifiedFromSourceTextNotLongerTranslation()
    {
        AssertEx.Equal(
            "砌筑内径",
            NarrativeClassificationTextPolicy.Select(
                "砌筑内径",
                "Masonry Internal Diameter"));
    }

    public static void CjkNarrativeRowsUseInformationWeightNotLatinCharacterCount()
    {
        NarrativeOccupancySample[] rows =
        [
            new("a", new Point2(10, 30), new Rect2(10, 28, 50, 32), "基础施工应符合设计要求", true),
            new("b", new Point2(10, 20), new Rect2(10, 18, 50, 22), "钢筋连接应满足规范要求", true),
            new("c", new Point2(10, 10), new Rect2(10, 8, 50, 12), "混凝土浇筑应连续进行", true)
        ];

        AssertEx.Equal(
            1,
            NarrativeOccupancyDetector.DetectGroups(rows, medianTextHeight: 4).Length);
    }

    public static void LayoutV2AssignsEveryChangedRecordOnce()
    {
        LayoutV2Input[] inputs =
        [
            new("a", "panel", LayoutV2Kind.Narrative, new Rect2(0, 10, 20, 20), new Rect2(0, 0, 100, 50), 2, []),
            new("b", "panel", LayoutV2Kind.Narrative, new Rect2(30, 10, 50, 20), new Rect2(0, 0, 100, 50), 2, [])
        ];

        LayoutV2Decision[] decisions = LayoutV2Planner.Plan(inputs);

        AssertEx.Equal(2, decisions.Length);
        AssertEx.Equal(2, decisions.Select(decision => decision.RecordId).Distinct().Count());
    }

    public static void LayoutV2ClampsNarrativeBeforeRightKeepout()
    {
        LayoutV2Input input = new(
            "note",
            "panel",
            LayoutV2Kind.Narrative,
            new Rect2(10, 10, 85, 40),
            new Rect2(0, 0, 100, 50),
            2,
            [new Rect2(80, 15, 95, 35)]);

        LayoutV2Decision decision = LayoutV2Planner.Plan([input]).Single();

        AssertEx.Equal(10d, decision.AllowedBounds.Left);
        AssertEx.Equal(76d, decision.AllowedBounds.Right);
        AssertEx.True(decision.ForceWrap);
        AssertEx.False(decision.ManualReview);
    }

    public static void LayoutV2ClampsFixedLabelBeforeUpperKeepout()
    {
        LayoutV2Decision decision = LayoutV2Planner.Plan(
        [
            new LayoutV2Input(
                "label",
                "label",
                LayoutV2Kind.FixedLabel,
                new Rect2(10, 0, 20, 5),
                new Rect2(0, -20, 40, 30),
                2,
                [new Rect2(5, 10, 25, 20)])
        ]).Single();

        AssertEx.True(decision.AllowedBounds.Contains(new Rect2(10, 0, 20, 5)));
        AssertEx.True(decision.AllowedBounds.Top < 10);
    }

    public static void LayoutV2PartitionsFixedLabelPeers()
    {
        LayoutV2Input[] inputs =
        [
            new("left", "row", LayoutV2Kind.FixedLabel, new Rect2(0, 10, 10, 20), new Rect2(0, 0, 40, 30), 2, []),
            new("right", "row", LayoutV2Kind.FixedLabel, new Rect2(20, 10, 30, 20), new Rect2(0, 0, 40, 30), 2, [])
        ];

        LayoutV2Decision[] decisions = LayoutV2Planner.Plan(inputs);
        Rect2 left = decisions.Single(decision => decision.RecordId == "left").AllowedBounds;
        Rect2 right = decisions.Single(decision => decision.RecordId == "right").AllowedBounds;

        AssertEx.True(left.Right <= right.Left);
        AssertEx.True(left.Contains(inputs[0].SourceBounds));
        AssertEx.True(right.Contains(inputs[1].SourceBounds));
    }

    public static void LayoutV2KeepsTableTextInsideParentCell()
    {
        Rect2 cell = new(0, 0, 30, 10);
        LayoutV2Input input = new(
            "cell",
            "cell-1",
            LayoutV2Kind.TableCell,
            new Rect2(2, 2, 12, 8),
            cell,
            2,
            []);

        LayoutV2Decision decision = LayoutV2Planner.Plan([input]).Single();

        AssertEx.True(cell.Contains(decision.AllowedBounds));
        AssertEx.True(decision.AllowedBounds.Contains(input.SourceBounds));
    }

    public static void LayoutV2RunsOneFitAndOneAudit()
    {
        AssertEx.Equal(1, LayoutV2ExecutionPolicy.FitPasses);
        AssertEx.Equal(1, LayoutV2ExecutionPolicy.AuditPasses);
        AssertEx.False(LayoutV2ExecutionPolicy.AllowsGlobalCorrection);
    }

    public static void LayoutV2NarrativeFragmentsShareOnePanel()
    {
        Rect2 panel = new(0, 0, 100, 50);
        LayoutV2Input[] inputs =
        [
            new("line-1", "notes", LayoutV2Kind.Narrative, new Rect2(10, 30, 40, 35), panel, 2, []),
            new("line-2", "notes", LayoutV2Kind.Narrative, new Rect2(20, 20, 45, 25), panel, 2, [])
        ];

        LayoutV2Decision[] decisions = LayoutV2Planner.Plan(inputs);

        AssertEx.Equal(decisions[0].AllowedBounds, decisions[1].AllowedBounds);
    }

    public static void LayoutV2ClassifiesLargeUnframedMTextAsNarrative()
    {
        AssertEx.Equal(
            LayoutV2Kind.Narrative,
            LayoutV2Classifier.Select(
                LayoutRegionKind.Unassigned,
                "AcDbMText",
                "Notes: Lubricate per manual before startup and inspect all guards before operation.",
                new Rect2(10, 10, 80, 30),
                2));
        AssertEx.Equal(
            LayoutV2Kind.FixedLabel,
            LayoutV2Classifier.Select(
                LayoutRegionKind.Unassigned,
                "AcDbMText",
                "Drive",
                new Rect2(10, 10, 20, 14),
                2));
    }

    public static void LayoutV2ClassifiesLongNotesInsideSheetFrameAsNarrative()
    {
        AssertEx.Equal(
            LayoutV2Kind.Narrative,
            LayoutV2Classifier.Select(
                LayoutRegionKind.ClosedFrame,
                "AcDbMText",
                "Technical Requirements: Before start-up, lubricate every rotating part and inspect all guards.",
                new Rect2(10, 10, 80, 30),
                2));
    }

    public static void TopologyCaptureExcludesErasedEntities()
    {
        AssertEx.False(TopologyCapturePolicy.ShouldCapture(isErased: true));
        AssertEx.True(TopologyCapturePolicy.ShouldCapture(isErased: false));
    }

    public static void TopologyCaptureExcludesRegeneratedDimensionText()
    {
        AssertEx.False(TopologyCapturePolicy.ShouldCaptureText("*D483", hasLayoutInput: false));
        AssertEx.True(TopologyCapturePolicy.ShouldCaptureText("*D483", hasLayoutInput: true));
        AssertEx.True(TopologyCapturePolicy.ShouldCaptureText("*U12", hasLayoutInput: false));
    }

    public static void HighGeometryContactIsSoftWhenTextOverlapGateIsClear()
    {
        HardGateResult hardGate = LayoutAuditPolicy.EvaluateHardGate(new HardGateInput(
            CjkResidualCount: 0,
            MissingRecordCount: 0,
            CandidateReopened: true,
            NonTextSignatureMatches: true,
            TableGridSignatureMatches: true,
            SourceAndWorkingPreserved: true));
        AssertEx.True(
            LayoutAuditPolicy.CanPublishCandidate(
                hardGate,
                [new LayoutRisk(LayoutRiskCode.GeometryOverlap, LayoutRiskLevel.High, 0.8)]));
        AssertEx.False(
            LayoutAuditPolicy.RequiresManualReview("geometry-overlap", "high"));
    }

    public static void TableCellAnchorMoveInsideCellIsMediumReview()
    {
        AssertEx.Equal(
            LayoutRiskLevel.Medium,
            TableCellAnchorDriftPolicy.Classify(candidateInsideCell: true));
        AssertEx.Equal(
            LayoutRiskLevel.High,
            TableCellAnchorDriftPolicy.Classify(candidateInsideCell: false));
    }

    private static void AssertInvalid(BatchValidationResult result, string expectedCode)
    {
        AssertEx.False(result.IsValid);
        AssertEx.Contains(expectedCode, result.Errors.Select(x => x.Code));
    }

    private static void AssertVerificationInvalid(VerificationResult result, string expectedCode)
    {
        AssertEx.False(result.IsValid);
        AssertEx.Contains(expectedCode, result.Errors.Select(x => x.Code));
    }
}

internal static class TestRunner
{
    public static int Run(IEnumerable<(string Name, Action Run)> tests)
    {
        int failures = 0;
        foreach ((string name, Action run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            }
        }

        return failures == 0 ? 0 : 1;
    }
}
