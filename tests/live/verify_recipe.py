from decimal import Decimal, InvalidOperation
from uuid import UUID
from verify import VerificationFailure

PREFERENCE = 'Please avoid sesame in the recipe'


def quantity(value):
    number = Decimal(str(value))
    if not number.is_finite() or number <= 0:
        raise VerificationFailure('invalid ingredient amount')
    text = format(number, 'f')
    return text.rstrip('0').rstrip('.') if '.' in text else text


def check_preference(rows, subject, witness):
    try:
        if len(rows) != 1:
            raise VerificationFailure('expected exactly one preference')
        row = rows[0]
        bot, chat, message = (int(witness[key]) for key in ('bot_id', 'chat_id', 'message_id'))
        if (str(UUID(row['subject_id'])) != str(UUID(subject)) or int(row['bot_id']) != bot
                or int(row['chat_id']) != chat or int(row['message_id']) != message
                or row['topic_id'] is not None or row['text'] != PREFERENCE):
            raise VerificationFailure('preference does not match source and expectation')
        if (int(witness['reply_bot_id']) != bot or int(witness['reply_to']) != message
                or witness['reply_text'] != 'Предпочтение сохранено: ' + PREFERENCE):
            raise VerificationFailure('preference reply is not linked')
    except (KeyError, TypeError, ValueError, InvalidOperation) as error:
        raise VerificationFailure('malformed preference evidence') from error


def check_recipe(rows, subject, pref_witness, witness):
    try:
        if len(rows) != 1:
            raise VerificationFailure('expected exactly one recipe')
        row = rows[0]
        bot, chat, message = (int(witness[key]) for key in ('bot_id', 'chat_id', 'message_id'))
        if (bot != int(pref_witness['bot_id']) or chat != int(pref_witness['chat_id'])
                or str(UUID(row['subject_id'])) != str(UUID(subject)) or int(row['bot_id']) != bot
                or int(row['chat_id']) != chat or int(row['message_id']) != message or row['topic_id'] is not None):
            raise VerificationFailure('recipe source or scope differs')
        title, ingredients, steps = row['title'], row['ingredients'], row['steps']
        if not isinstance(title, str) or not title.strip() or not ingredients or not steps:
            raise VerificationFailure('empty recipe content')
        if any(not isinstance(s, str) or not s.strip() for s in steps):
            raise VerificationFailure('empty preparation step')
        lines = []
        for ingredient in ingredients:
            name = ingredient['Name']
            if not isinstance(name, str) or not name.strip():
                raise VerificationFailure('empty ingredient')
            lines.append(f"- {name}: {quantity(ingredient['Grams'])} г")
        expected = (f"Рецепт #{int(row['id'])}\nИсточник: {bot}/{chat}/{message}\n{title}\n"
                    + 'Ингредиенты:\n' + '\n'.join(lines) + '\nПриготовление:\n' + '\n'.join(steps))
        if (int(witness['reply_bot_id']) != bot or int(witness['reply_to']) != message
                or witness['reply_text'] != expected):
            raise VerificationFailure('recipe reply differs from saved source and content')
        return row
    except (KeyError, TypeError, ValueError, InvalidOperation) as error:
        raise VerificationFailure('malformed recipe evidence') from error


def check_shopping(row, recipe_witness, witness):
    try:
        bot, chat, source = (int(recipe_witness[k]) for k in ('bot_id', 'chat_id', 'message_id'))
        message = int(witness['message_id'])
        lines = [f"- {i['Name']}: {quantity(i['Grams'])} г" for i in row['ingredients']]
        expected = (f"Список покупок для рецепта #{int(row['id'])}\nИсточник: {bot}/{chat}/{source}\n"
                    + '\n'.join(lines))
        if (int(witness['chat_id']) != chat or int(witness['reply_bot_id']) != bot
                or int(witness['reply_to']) != message or witness['reply_text'] != expected):
            raise VerificationFailure('shopping differs from selected saved recipe')
    except (KeyError, TypeError, ValueError, InvalidOperation) as error:
        raise VerificationFailure('malformed shopping evidence') from error
