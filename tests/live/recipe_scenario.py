import json
from decimal import Decimal
from verify import VerificationFailure
from verify_recipe import PREFERENCE, check_preference, check_recipe, check_shopping

PREF_SQL = '''SELECT COALESCE(json_agg(row_to_json(t)), '[]'::json)::text FROM (
SELECT "SubjectId" AS subject_id, "BotId" AS bot_id, "ChatId" AS chat_id,
"MessageId" AS message_id, "TopicId" AS topic_id, "Text" AS text FROM food_preferences ORDER BY "Id") t;'''
RECIPE_SQL = '''SELECT COALESCE(json_agg(row_to_json(t)), '[]'::json)::text FROM (
SELECT "Id" AS id, "SubjectId" AS subject_id, "BotId" AS bot_id, "ChatId" AS chat_id,
"MessageId" AS message_id, "TopicId" AS topic_id, "Title" AS title,
"IngredientsJson" AS ingredients, "StepsJson" AS steps FROM saved_recipes ORDER BY "Id") t;'''


def execute(container, subject, child, restart, run, witness, receipt, save):
    def query(sql):
        return json.loads(run(['docker', 'exec', container, 'psql', '-U', 'postgres', '-d', 'runner',
                               '-t', '-A', '-c', sql]), parse_float=Decimal)

    if query(PREF_SQL) or query(RECIPE_SQL):
        raise VerificationFailure('isolated database was not empty')
    pref = witness('Send /prefer ' + PREFERENCE + ' in the selected General topic; enter source witness JSON:')
    receipt['preference_witness'] = pref
    initial = query(PREF_SQL)
    receipt['preference_before_restart'] = initial
    save()
    check_preference(initial, subject, pref)
    child.stop()
    if child.requests != 0 or child.recipe_requests != 0:
        raise VerificationFailure('preference triggered a model request')
    child = restart()
    after = query(PREF_SQL)
    receipt['preference_after_restart'] = after
    save()
    check_preference(after, subject, pref)
    if after != initial:
        raise VerificationFailure('preference changed across restart')
    recipe = witness('Send /recipe in the same topic; enter source witness JSON:')
    receipt['recipe_witness'] = recipe
    child.wait_for('recipe_requested', 10)
    selected = check_recipe(query(RECIPE_SQL), subject, pref, recipe)
    receipt['selected_recipe'] = selected
    save()
    semantic = witness('Independent verification agent: enter {"semantic_check":"pass"} only after inspecting the saved synthetic recipe against the declared preference and coherence:')
    receipt['semantic_check'] = semantic
    save()
    if semantic.get('semantic_check') != 'pass':
        raise VerificationFailure('independent semantic inspection failed or unavailable')
    shopping = witness('Send /shopping in the same topic; enter linked reply witness JSON:')
    receipt['shopping_witness'] = shopping
    save()
    child.stop()
    if child.recipe_requests != 1 or child.requests != 0:
        raise VerificationFailure('unexpected OpenAI request count')
    check_shopping(selected, recipe, shopping)
    receipt['outcome'] = 'pass'
