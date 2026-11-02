from copy import deepcopy
import unittest
from verify import VerificationFailure
from verify_recipe import PREFERENCE, check_preference, check_recipe, check_shopping

SUBJECT = '11111111-1111-1111-1111-111111111111'
PREF = {'subject_id': SUBJECT, 'bot_id': 111, 'chat_id': 222, 'message_id': 333,
        'topic_id': None, 'text': PREFERENCE}
PW = {'bot_id': 111, 'chat_id': 222, 'message_id': 333, 'reply_bot_id': 111, 'reply_to': 333,
      'reply_text': 'Предпочтение сохранено: ' + PREFERENCE}
ROW = {'id': 9, 'subject_id': SUBJECT, 'bot_id': 111, 'chat_id': 222, 'message_id': 334,
       'topic_id': None, 'title': 'Блюдо', 'ingredients': [{'Name': 'Овёс', 'Grams': 80},
       {'Name': 'Вода', 'Grams': 200}], 'steps': ['Приготовить.']}
RW = {'bot_id': 111, 'chat_id': 222, 'message_id': 334, 'reply_bot_id': 111, 'reply_to': 334,
      'reply_text': 'Рецепт #9\nИсточник: 111/222/334\nБлюдо\nИнгредиенты:\n- Овёс: 80 г\n- Вода: 200 г\nПриготовление:\nПриготовить.'}
SW = {'chat_id': 222, 'message_id': 335, 'reply_bot_id': 111, 'reply_to': 335,
      'reply_text': 'Список покупок для рецепта #9\nИсточник: 111/222/334\n- Овёс: 80 г\n- Вода: 200 г'}


class RecipeVerifierTests(unittest.TestCase):
    def test_known_valid_evidence(self):
        check_preference([PREF], SUBJECT, PW)
        row = check_recipe([ROW], SUBJECT, PW, RW)
        check_shopping(row, RW, SW)

    def test_missing_preference_is_rejected(self):
        with self.assertRaises(VerificationFailure):
            check_preference([], SUBJECT, PW)

    def test_wrong_shopping_gram_amount_is_rejected(self):
        bad = dict(SW, reply_text=SW['reply_text'].replace('80 г', '81 г'))
        with self.assertRaises(VerificationFailure):
            check_shopping(ROW, RW, bad)

    def test_other_saved_recipe_is_rejected(self):
        other = deepcopy(ROW)
        other['id'] = 10
        with self.assertRaises(VerificationFailure):
            check_shopping(other, RW, SW)


if __name__ == '__main__':
    unittest.main()
